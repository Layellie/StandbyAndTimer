using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

        #endregion

        #region Global Variables & Structures

        // Target system timer resolution (5000 = 0.5ms)
        const uint TargetResolution = 5000;

        // Tracking variables
        int purgeCount = 0;
        PerformanceCounter standbyCounter;

        // Multi-Game Optimization Variables
        List<string> gamePaths = new List<string>();
        // Tracks optimized process IDs to prevent redundant operations
        HashSet<int> optimizedPIDs = new HashSet<int>();

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
            this.FormClosing += new FormClosingEventHandler(Form1_FormClosing);
            standbyCounter = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes", null);
            timer1.Tick += new EventHandler(timer1_Tick);
        }

        #region Main Form Events

        private void Form1_Load(object sender, EventArgs e)
        {
            // Set form to start at the center of the screen
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BringToFront();

            // Set process priority to High
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

            // Lock system timer to 0.5ms globally
            uint currentRes;
            NtSetTimerResolution(TargetResolution, true, out currentRes);

            // Load saved user preferences from Windows Registry
            RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\StandbyAndTimer");
            if (key != null)
            {
                txtStandbyLimit.Text = key.GetValue("StandbyLimit", "1024").ToString();
                txtFreeLimit.Text = key.GetValue("FreeLimit", "1024").ToString();

                // Restore multi-game list from Registry
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

            // Attach event handlers for dynamic saving to Registry
            txtStandbyLimit.TextChanged += new EventHandler(SaveSettingsToRegistry);
            txtFreeLimit.TextChanged += new EventHandler(SaveSettingsToRegistry);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // This prompt triggers only when the user clicks the Close (X) button
            if (e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult result = MessageBox.Show(
                    "Do you want to exit the application completely?\n\n" +
                    "• Select 'Yes' to Close completely.\n" +
                    "• Select 'No' to Minimize to System Tray (keeps it running).",
                    "StandbyAndTimer - Exit Confirmation",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    // Allow the application to close
                    e.Cancel = false;
                }
                else if (result == DialogResult.No)
                {
                    // Minimize to system tray
                    e.Cancel = true;
                    this.Hide();
                    notifyIcon1.Visible = true;
                }
                else
                {
                    // Cancel clicked, do nothing and stay on the screen
                    e.Cancel = true;
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Handle silent startup from Task Scheduler
            if (Environment.GetCommandLineArgs().Contains("-hidden"))
            {
                this.Hide();
                notifyIcon1.Visible = true;
                chkAutoStart.Checked = true;
            }
        }

        #endregion

        #region Core Optimization Logic (Timer, Memory, CPU Affinity)

        private void timer1_Tick(object sender, EventArgs e)
        {
            // 1. Fetch current memory status
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref memStatus);

            long totalRamMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
            long freeRamMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
            long standbyRamMB = (long)(standbyCounter.NextValue() / (1024 * 1024));

            // Update UI labels
            lblTotalRAM.Text = totalRamMB.ToString() + " MB";
            lblStandby.Text = standbyRamMB.ToString() + " MB";
            lblFreeRAM.Text = freeRamMB.ToString() + " MB";

            // 2. Evaluate auto-purge conditions
            int limitStandby = 0;
            int limitFree = 0;
            int.TryParse(txtStandbyLimit.Text, out limitStandby);
            int.TryParse(txtFreeLimit.Text, out limitFree);

            if (standbyRamMB >= limitStandby && freeRamMB <= limitFree && limitStandby > 0)
            {
                PurgeStandbyList();
            }

            // 3. Monitor and optimize target game processes
            CheckAndOptimizeGame();
        }

        private void CheckAndOptimizeGame()
        {
            if (!chkGameMode.Checked || gamePaths.Count == 0) return;

            foreach (string path in gamePaths)
            {
                string procName = Path.GetFileNameWithoutExtension(path);
                Process[] processes = Process.GetProcessesByName(procName);

                if (processes.Length > 0)
                {
                    foreach (var p in processes)
                    {
                        if (!optimizedPIDs.Contains(p.Id))
                        {
                            try
                            {
                                p.PriorityClass = ProcessPriorityClass.High;

                                // Dynamic CPU Affinity
                                int cpuCount = Environment.ProcessorCount;
                                long affinityMask = (1L << cpuCount) - 1;
                                p.ProcessorAffinity = (IntPtr)affinityMask;

                                optimizedPIDs.Add(p.Id);
                            }
                            catch { /* Skip if access is denied */ }
                        }
                    }
                }
            }

            // Cleanup: Remove terminated processes
            optimizedPIDs.RemoveWhere(pid => {
                try { Process.GetProcessById(pid); return false; }
                catch { return true; }
            });
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
                    tp.Attributes = 2; // SE_PRIVILEGE_ENABLED
                    LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", ref tp.Luid);
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }

                int command = 4; // MemoryPurgeStandbyList
                IntPtr pCommand = Marshal.AllocHGlobal(Marshal.SizeOf(command));
                Marshal.WriteInt32(pCommand, command);

                int result = NtSetSystemInformation(80, pCommand, Marshal.SizeOf(command));
                Marshal.FreeHGlobal(pCommand);

                if (result == 0)
                {
                    purgeCount++;
                    lblPurgeCount.Text = purgeCount.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Purge Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
                MessageBox.Show("Game removed from tracking.", "Success");
            }
            else
            {
                MessageBox.Show("Please select a game from the list to remove.", "Warning");
            }
        }

        private void SaveSettingsToRegistry(object sender, EventArgs e)
        {
            RegistryKey key = Registry.CurrentUser.CreateSubKey("SOFTWARE\\StandbyAndTimer");
            key.SetValue("StandbyLimit", txtStandbyLimit.Text);
            key.SetValue("FreeLimit", txtFreeLimit.Text);
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
            uint currentRes;
            int result = NtSetTimerResolution(TargetResolution, true, out currentRes);
            if (result == 0) MessageBox.Show("0.5ms Activated!", "Success");
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
                               "2. Nukes Standby RAM\n" +
                               "3. CPU Affinity for Multi-Games\n\n" +
                               "Dev: [LAYE77IE]";
            MessageBox.Show(aboutText, "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.Show();
            notifyIcon1.Visible = false;
        }

        private void label1_Click(object sender, EventArgs e) { }
        private void exitToolStripMenuItem_Click(object sender, EventArgs e) => Application.Exit();
        private void contextMenuStrip1_Opening(object sender, System.ComponentModel.CancelEventArgs e) { }

        #endregion
    }
}