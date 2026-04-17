namespace StandbyAndTimer
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            button1 = new Button();
            notifyIcon1 = new NotifyIcon(components);
            contextMenuStrip1 = new ContextMenuStrip(components);
            çIKIŞToolStripMenuItem = new ToolStripMenuItem();
            lblBaslık1 = new Label();
            lblBaslık2 = new Label();
            lblBaslık3 = new Label();
            txtStandbyLimit = new TextBox();
            txtFreeLimit = new TextBox();
            btnManuelTemizle = new Button();
            timer1 = new System.Windows.Forms.Timer(components);
            lblTotalRAM = new Label();
            lblFreeRAM = new Label();
            label4 = new Label();
            label5 = new Label();
            lblStandby = new Label();
            lblPurgeCount = new Label();
            chkAutoStart = new CheckBox();
            btnBilgi = new Button();
            txtGamePath = new TextBox();
            btnOyunSec = new Button();
            chkGameMode = new CheckBox();
            lstGames = new ListBox();
            btnSil = new Button();
            contextMenuStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // button1
            // 
            button1.FlatStyle = FlatStyle.Flat;
            button1.Font = new Font("Times New Roman", 12F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            button1.ForeColor = Color.Yellow;
            button1.Location = new Point(307, 347);
            button1.Margin = new Padding(8);
            button1.Name = "button1";
            button1.Size = new Size(80, 25);
            button1.TabIndex = 0;
            button1.Text = "TIMER";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // notifyIcon1
            // 
            notifyIcon1.BalloonTipIcon = ToolTipIcon.Info;
            notifyIcon1.ContextMenuStrip = contextMenuStrip1;
            notifyIcon1.Icon = (Icon)resources.GetObject("notifyIcon1.Icon");
            notifyIcon1.Text = "StandbyAndTimer";
            notifyIcon1.Visible = true;
            notifyIcon1.MouseDoubleClick += notifyIcon1_MouseDoubleClick;
            // 
            // contextMenuStrip1
            // 
            contextMenuStrip1.Items.AddRange(new ToolStripItem[] { çIKIŞToolStripMenuItem });
            contextMenuStrip1.Name = "contextMenuStrip1";
            contextMenuStrip1.Size = new Size(181, 48);
            contextMenuStrip1.Opening += contextMenuStrip1_Opening;
            // 
            // çIKIŞToolStripMenuItem
            // 
            çIKIŞToolStripMenuItem.Name = "çIKIŞToolStripMenuItem";
            çIKIŞToolStripMenuItem.Size = new Size(180, 22);
            çIKIŞToolStripMenuItem.Text = "EXIT";
            çIKIŞToolStripMenuItem.Click += exitToolStripMenuItem_Click;
            // 
            // lblBaslık1
            // 
            lblBaslık1.AutoSize = true;
            lblBaslık1.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            lblBaslık1.Location = new Point(12, 14);
            lblBaslık1.Name = "lblBaslık1";
            lblBaslık1.Size = new Size(126, 16);
            lblBaslık1.TabIndex = 1;
            lblBaslık1.Text = "Total system memory:";
            // 
            // lblBaslık2
            // 
            lblBaslık2.AutoSize = true;
            lblBaslık2.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            lblBaslık2.Location = new Point(12, 43);
            lblBaslık2.Name = "lblBaslık2";
            lblBaslık2.Size = new Size(185, 16);
            lblBaslık2.TabIndex = 2;
            lblBaslık2.Text = "Standby list & system working set:";
            // 
            // lblBaslık3
            // 
            lblBaslık3.AutoSize = true;
            lblBaslık3.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            lblBaslık3.Location = new Point(12, 72);
            lblBaslık3.Name = "lblBaslık3";
            lblBaslık3.Size = new Size(86, 16);
            lblBaslık3.TabIndex = 3;
            lblBaslık3.Text = "Free Memory:";
            // 
            // txtStandbyLimit
            // 
            txtStandbyLimit.BackColor = Color.Yellow;
            txtStandbyLimit.BorderStyle = BorderStyle.FixedSingle;
            txtStandbyLimit.Font = new Font("Times New Roman", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 162);
            txtStandbyLimit.Location = new Point(180, 99);
            txtStandbyLimit.Name = "txtStandbyLimit";
            txtStandbyLimit.Size = new Size(88, 25);
            txtStandbyLimit.TabIndex = 4;
            // 
            // txtFreeLimit
            // 
            txtFreeLimit.BackColor = Color.Yellow;
            txtFreeLimit.BorderStyle = BorderStyle.FixedSingle;
            txtFreeLimit.Font = new Font("Times New Roman", 11.25F, FontStyle.Bold, GraphicsUnit.Point, 162);
            txtFreeLimit.ForeColor = SystemColors.WindowText;
            txtFreeLimit.Location = new Point(208, 127);
            txtFreeLimit.Name = "txtFreeLimit";
            txtFreeLimit.Size = new Size(88, 25);
            txtFreeLimit.TabIndex = 5;
            // 
            // btnManuelTemizle
            // 
            btnManuelTemizle.BackColor = SystemColors.WindowText;
            btnManuelTemizle.FlatStyle = FlatStyle.Flat;
            btnManuelTemizle.Location = new Point(221, 167);
            btnManuelTemizle.Name = "btnManuelTemizle";
            btnManuelTemizle.Size = new Size(163, 25);
            btnManuelTemizle.TabIndex = 6;
            btnManuelTemizle.Text = "Purge Standby list";
            btnManuelTemizle.UseVisualStyleBackColor = false;
            btnManuelTemizle.Click += btnManuelTemizle_Click;
            // 
            // timer1
            // 
            timer1.Enabled = true;
            timer1.Interval = 1000;
            // 
            // lblTotalRAM
            // 
            lblTotalRAM.AutoSize = true;
            lblTotalRAM.Location = new Point(144, 15);
            lblTotalRAM.Name = "lblTotalRAM";
            lblTotalRAM.Size = new Size(0, 15);
            lblTotalRAM.TabIndex = 7;
            // 
            // lblFreeRAM
            // 
            lblFreeRAM.AutoSize = true;
            lblFreeRAM.Location = new Point(104, 73);
            lblFreeRAM.Name = "lblFreeRAM";
            lblFreeRAM.Size = new Size(0, 15);
            lblFreeRAM.TabIndex = 9;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            label4.Location = new Point(12, 103);
            label4.Name = "label4";
            label4.Size = new Size(162, 16);
            label4.TabIndex = 11;
            label4.Text = "The list size is at least (MB):";
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            label5.Location = new Point(12, 131);
            label5.Name = "label5";
            label5.Size = new Size(190, 16);
            label5.TabIndex = 12;
            label5.Text = "Free memory is lower than (MB):";
            // 
            // lblStandby
            // 
            lblStandby.AutoSize = true;
            lblStandby.Location = new Point(197, 43);
            lblStandby.Name = "lblStandby";
            lblStandby.Size = new Size(0, 15);
            lblStandby.TabIndex = 14;
            // 
            // lblPurgeCount
            // 
            lblPurgeCount.AutoSize = true;
            lblPurgeCount.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            lblPurgeCount.Location = new Point(361, 172);
            lblPurgeCount.Name = "lblPurgeCount";
            lblPurgeCount.Size = new Size(14, 16);
            lblPurgeCount.TabIndex = 15;
            lblPurgeCount.Text = "0";
            lblPurgeCount.Click += label1_Click;
            // 
            // chkAutoStart
            // 
            chkAutoStart.AutoSize = true;
            chkAutoStart.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            chkAutoStart.Location = new Point(12, 352);
            chkAutoStart.Name = "chkAutoStart";
            chkAutoStart.Size = new Size(199, 20);
            chkAutoStart.TabIndex = 16;
            chkAutoStart.Text = "Launch on Startup (Minimized)";
            chkAutoStart.UseVisualStyleBackColor = true;
            chkAutoStart.CheckedChanged += chkAutoStart_CheckedChanged;
            // 
            // btnBilgi
            // 
            btnBilgi.BackColor = SystemColors.WindowText;
            btnBilgi.FlatStyle = FlatStyle.Flat;
            btnBilgi.Font = new Font("Times New Roman", 12F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point, 162);
            btnBilgi.ForeColor = Color.Yellow;
            btnBilgi.Location = new Point(224, 347);
            btnBilgi.Name = "btnBilgi";
            btnBilgi.Size = new Size(80, 25);
            btnBilgi.TabIndex = 17;
            btnBilgi.Text = "?";
            btnBilgi.UseVisualStyleBackColor = false;
            btnBilgi.Click += btnBilgi_Click;
            // 
            // txtGamePath
            // 
            txtGamePath.BackColor = Color.Yellow;
            txtGamePath.BorderStyle = BorderStyle.FixedSingle;
            txtGamePath.Location = new Point(12, 260);
            txtGamePath.Name = "txtGamePath";
            txtGamePath.ReadOnly = true;
            txtGamePath.Size = new Size(375, 22);
            txtGamePath.TabIndex = 18;
            // 
            // btnOyunSec
            // 
            btnOyunSec.FlatStyle = FlatStyle.Flat;
            btnOyunSec.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold, GraphicsUnit.Point, 162);
            btnOyunSec.Location = new Point(312, 288);
            btnOyunSec.Name = "btnOyunSec";
            btnOyunSec.Size = new Size(75, 22);
            btnOyunSec.TabIndex = 19;
            btnOyunSec.Text = "ADD";
            btnOyunSec.UseVisualStyleBackColor = true;
            btnOyunSec.Click += btnOyunSec_Click;
            // 
            // chkGameMode
            // 
            chkGameMode.AutoSize = true;
            chkGameMode.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold, GraphicsUnit.Point, 162);
            chkGameMode.Location = new Point(12, 291);
            chkGameMode.Name = "chkGameMode";
            chkGameMode.Size = new Size(102, 19);
            chkGameMode.TabIndex = 20;
            chkGameMode.Text = "GAME MODE";
            chkGameMode.UseVisualStyleBackColor = true;
            // 
            // lstGames
            // 
            lstGames.BackColor = Color.Yellow;
            lstGames.FormattingEnabled = true;
            lstGames.Location = new Point(12, 205);
            lstGames.Name = "lstGames";
            lstGames.Size = new Size(375, 49);
            lstGames.TabIndex = 21;
            // 
            // btnSil
            // 
            btnSil.FlatStyle = FlatStyle.Flat;
            btnSil.Font = new Font("Times New Roman", 9.75F, FontStyle.Bold, GraphicsUnit.Point, 162);
            btnSil.Location = new Point(224, 288);
            btnSil.Name = "btnSil";
            btnSil.Size = new Size(75, 22);
            btnSil.TabIndex = 23;
            btnSil.Text = "DELL";
            btnSil.UseVisualStyleBackColor = true;
            btnSil.Click += btnSil_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = SystemColors.WindowText;
            ClientSize = new Size(396, 384);
            Controls.Add(btnSil);
            Controls.Add(lstGames);
            Controls.Add(chkGameMode);
            Controls.Add(btnOyunSec);
            Controls.Add(txtGamePath);
            Controls.Add(btnBilgi);
            Controls.Add(chkAutoStart);
            Controls.Add(lblPurgeCount);
            Controls.Add(lblStandby);
            Controls.Add(label5);
            Controls.Add(label4);
            Controls.Add(lblFreeRAM);
            Controls.Add(lblTotalRAM);
            Controls.Add(btnManuelTemizle);
            Controls.Add(txtFreeLimit);
            Controls.Add(txtStandbyLimit);
            Controls.Add(lblBaslık3);
            Controls.Add(lblBaslık2);
            Controls.Add(lblBaslık1);
            Controls.Add(button1);
            Font = new Font("Times New Roman", 9.75F, FontStyle.Regular, GraphicsUnit.Point, 0);
            ForeColor = Color.Yellow;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Icon = (Icon)resources.GetObject("$this.Icon");
            Margin = new Padding(4);
            Name = "Form1";
            Text = "StandbyAndTimer";
            FormClosing += Form1_FormClosing;
            Load += Form1_Load;
            contextMenuStrip1.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button button1;
        private NotifyIcon notifyIcon1;
        private ContextMenuStrip contextMenuStrip1;
        private ToolStripMenuItem çIKIŞToolStripMenuItem;
        private Label lblBaslık1;
        private Label lblBaslık2;
        private Label lblBaslık3;
        private TextBox txtStandbyLimit;
        private TextBox txtFreeLimit;
        private Button btnManuelTemizle;
        private System.Windows.Forms.Timer timer1;
        private Label lblTotalRAM;
        private Label lblFreeRAM;
        private Label label4;
        private Label label5;
        private Label lblStandby;
        private Label lblPurgeCount;
        private CheckBox chkAutoStart;
        private Button btnBilgi;
        private TextBox txtGamePath;
        private Button btnOyunSec;
        private CheckBox chkGameMode;
        private ListBox lstGames;
        private Button btnSil;
    }
}
