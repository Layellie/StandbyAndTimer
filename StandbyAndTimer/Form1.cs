using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StandbyAndTimer
{
    public partial class Form1 : Form
    {
        #region Native Windows API Imports

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr h, int acc, ref IntPtr phtok);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool LookupPrivilegeValue(string host, string name, ref long pluid);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool AdjustTokenPrivileges(IntPtr htok, bool disall, ref TOKEN_PRIVILEGES newst, int len, IntPtr prev, IntPtr relen);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref uint processInformation, int processInformationSize);

        [DllImport("avrt.dll", SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string TaskName, ref uint TaskIndex);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        #endregion

        #region Global Variables & Structures

        const uint TargetResolution = 5000;
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;

        int purgeCount = 0;
        PerformanceCounter standbyCounter;

        List<string> gamePaths = new List<string>();
        Dictionary<int, Process> optimizedProcesses = new Dictionary<int, Process>();

        int activeStandbyLimit = 1024;
        int activeFreeLimit = 1024;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        #endregion

        public Form1()
        {
            InitializeComponent();
            standbyCounter = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes", null);
        }

        #region Main Form Events

        private void Form1_Load(object sender, EventArgs e)
        {
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BringToFront();

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

            uint taskIndex = 0;
            AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

            uint bypassFlag = 1;
            SetProcessInformation(Process.GetCurrentProcess().Handle, 34, ref bypassFlag, sizeof(uint));

            uint currentRes;
            NtSetTimerResolution(TargetResolution, true, out currentRes);

            RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\StandbyAndTimer");
            if (key != null)
            {
                txtStandbyLimit.Text = key.GetValue("StandbyLimit", "1024").ToString();
                txtFreeLimit.Text = key.GetValue("FreeLimit", "1024").ToString();

                int.TryParse(txtStandbyLimit.Text, out activeStandbyLimit);
                int.TryParse(txtFreeLimit.Text, out activeFreeLimit);

                string savedPaths = key.GetValue("GamePaths", "").ToString();
                if (!string.IsNullOrEmpty(savedPaths))
                {
                    gamePaths = savedPaths.Split(';').ToList();
                    foreach (string path in gamePaths)
                    {
                        if (File.Exists(path))
                        {
                            lstGames.Items.Add(Path.GetFileNameWithoutExtension(path));
                        }
                    }
                }
            }

            txtStandbyLimit.TextChanged += new EventHandler(UpdateLimits);
            txtFreeLimit.TextChanged += new EventHandler(UpdateLimits);

            StartEngine();
        }

        private void UpdateLimits(object sender, EventArgs e)
        {
            int.TryParse(txtStandbyLimit.Text, out activeStandbyLimit);
            int.TryParse(txtFreeLimit.Text, out activeFreeLimit);

            RegistryKey key = Registry.CurrentUser.CreateSubKey("SOFTWARE\\StandbyAndTimer");
            key.SetValue("StandbyLimit", txtStandbyLimit.Text);
            key.SetValue("FreeLimit", txtFreeLimit.Text);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult result = MessageBox.Show(
                    "Do you want to exit the application completely?\n\n" +
                    "• Click 'Yes' to close completely.\n" +
                    "• Click 'No' to minimize to the system tray (keeps running in background).",
                    "StandbyAndTimer - Exit Confirmation",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    e.Cancel = false;
                    notifyIcon1.Visible = false;
                    Environment.Exit(0);
                }
                else if (result == DialogResult.No)
                {
                    e.Cancel = true;
                    this.Opacity = 0;
                    this.ShowInTaskbar = false;
                    this.Location = new System.Drawing.Point(-32000, -32000);
                    notifyIcon1.Visible = true;
                }
                else
                {
                    e.Cancel = true;
                }
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            notifyIcon1.Visible = false;
            Environment.Exit(0);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (Environment.GetCommandLineArgs().Contains("-hidden"))
            {
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                this.Location = new System.Drawing.Point(-32000, -32000);
                notifyIcon1.Visible = true;
                chkAutoStart.Checked = true;
            }
        }

        #endregion

        #region Core Optimization Logic (Background Thread, Memory, CPU Affinity)

        private void StartEngine()
        {
            Thread engineThread = new Thread(() =>
            {
                uint taskIndex = 0;
                AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

                while (true)
                {
                    uint currentRes;
                    NtSetTimerResolution(TargetResolution, true, out currentRes);

                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                    GlobalMemoryStatusEx(ref memStatus);

                    long totalRamMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                    long freeRamMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                    long standbyRamMB = 0;

                    try { standbyRamMB = (long)(standbyCounter.NextValue() / (1024 * 1024)); } catch { }

                    if (standbyRamMB >= activeStandbyLimit && freeRamMB <= activeFreeLimit && activeStandbyLimit > 0)
                    {
                        PurgeStandbyList();
                    }

                    CheckAndOptimizeGame();

                    if (this.IsHandleCreated && !this.IsDisposed)
                    {
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            lblTotalRAM.Text = totalRamMB.ToString() + " MB";
                            lblStandby.Text = standbyRamMB.ToString() + " MB";
                            lblFreeRAM.Text = freeRamMB.ToString() + " MB";
                            lblPurgeCount.Text = purgeCount.ToString();
                        });
                    }
                    Thread.Sleep(1000);
                }
            });
            engineThread.IsBackground = true;
            engineThread.Priority = ThreadPriority.Highest;
            engineThread.Start();
        }

        private void CheckAndOptimizeGame()
        {
            if (!chkGameMode.Checked || gamePaths.Count == 0) return;

            foreach (string path in gamePaths)
            {
                string procName = Path.GetFileNameWithoutExtension(path);
                Process[] processes = Process.GetProcessesByName(procName);

                foreach (var p in processes)
                {
                    if (!optimizedProcesses.ContainsKey(p.Id))
                    {
                        try
                        {
                            p.PriorityClass = ProcessPriorityClass.High;

                            int cpuCount = Environment.ProcessorCount;
                            long affinityMask = (1L << cpuCount) - 1;
                            p.ProcessorAffinity = (IntPtr)affinityMask;

                            optimizedProcesses.Add(p.Id, p);
                        }
                        catch { }
                    }
                }
            }

            var deadPIDs = optimizedProcesses.Where(kvp => kvp.Value.HasExited).Select(kvp => kvp.Key).ToList();
            foreach (var pid in deadPIDs)
            {
                optimizedProcesses[pid].Dispose();
                optimizedProcesses.Remove(pid);
            }
        }

        private void PurgeStandbyList()
        {
            try
            {
                IntPtr hToken = IntPtr.Zero;
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028, ref hToken))
                {
                    TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                    tp.PrivilegeCount = 1;
                    tp.Attributes = 2;
                    LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", ref tp.Luid);
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }

                int command = 4;
                IntPtr pCommand = Marshal.AllocHGlobal(Marshal.SizeOf(command));
                Marshal.WriteInt32(pCommand, command);

                int result = NtSetSystemInformation(80, pCommand, Marshal.SizeOf(command));
                Marshal.FreeHGlobal(pCommand);

                if (result == 0)
                {
                    purgeCount++;
                }
            }
            catch { }
        }

        #endregion

        #region UI Component Actions

        private void btnOyunSec_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Select Target Game Executable";
                ofd.Filter = "Executable Files (*.exe)|*.exe";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    if (!gamePaths.Contains(ofd.FileName))
                    {
                        gamePaths.Add(ofd.FileName);
                        lstGames.Items.Add(Path.GetFileNameWithoutExtension(ofd.FileName));
                        SaveGamesToRegistry();
                        MessageBox.Show("Game added to the optimization list!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }

        private void btnSil_Click(object sender, EventArgs e)
        {
            if (lstGames.SelectedIndex != -1)
            {
                int index = lstGames.SelectedIndex;
                gamePaths.RemoveAt(index);
                lstGames.Items.RemoveAt(index);
                SaveGamesToRegistry();
                MessageBox.Show("Game removed from the tracking list.", "Success");
            }
            else
            {
                MessageBox.Show("Please select a game from the list to remove.", "Warning");
            }
        }

        private void SaveGamesToRegistry()
        {
            RegistryKey key = Registry.CurrentUser.CreateSubKey("SOFTWARE\\StandbyAndTimer");
            if (gamePaths.Count > 0)
                key.SetValue("GamePaths", string.Join(";", gamePaths));
            else
                key.DeleteValue("GamePaths", false);
        }

        private void btnManuelTemizle_Click(object sender, EventArgs e) => PurgeStandbyList();

        private void button1_Click(object sender, EventArgs e)
        {
            uint bypassFlag = 1;
            SetProcessInformation(Process.GetCurrentProcess().Handle, 34, ref bypassFlag, sizeof(uint));

            uint currentRes;
            int result = NtSetTimerResolution(TargetResolution, true, out currentRes);

            if (result == 0)
            {
                double gercekMs = currentRes / 10000.0;
                MessageBox.Show($"Timer Active!\nActual Value: {gercekMs} ms", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void chkAutoStart_CheckedChanged(object sender, EventArgs e)
        {
            string taskName = "StandbyAndTimer_AutoStart";
            string exePath = Application.ExecutablePath;
            try
            {
                string args = chkAutoStart.Checked
                    ? $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\" -hidden\" /sc onlogon /rl highest /f"
                    : $"/delete /tn \"{taskName}\" /f";

                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args) { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show("Task Error: " + ex.Message); }
        }

        private void btnBilgi_Click(object sender, EventArgs e)
        {
            string aboutText = "StandbyAndTimer Optimization Tool\n\n" +
                               "1. Locks System Timer to 0.5ms\n" +
                               "2. Purges Standby Memory\n" +
                               "3. CPU Affinity for Multi-Games\n\n" +
                               "--- Recommended Purge Settings ---\n" +
                               "• 4 GB RAM: List Size: 512 | Free Memory: 512\n" +
                               "• 8 GB RAM: List Size: 1024 | Free Memory: 1024\n" +
                               "• 16 GB RAM: List Size: 2048 | Free Memory: 1024\n" +
                               "• 32 GB RAM: List Size: 4096 | Free Memory: 2048\n" +
                               "• 64 GB RAM: List Size: 8192 | Free Memory: 2048\n\n" +
                               "Developer: [LAYE77IE]";
            MessageBox.Show(aboutText, "About and Usage Guide", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.Opacity = 1;
            this.ShowInTaskbar = true;
            this.CenterToScreen();
            this.BringToFront();
            notifyIcon1.Visible = false;
        }

        private void label1_Click(object sender, EventArgs e) { }
        private void contextMenuStrip1_Opening(object sender, System.ComponentModel.CancelEventArgs e) { }

        #endregion
    }
}