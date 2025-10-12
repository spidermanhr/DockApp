using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text.Json;
using System.Linq;
using System.Threading; // Potrebno za Thread.Sleep
using Microsoft.Win32;

namespace DockApp
{
    // Klasa za predstavljanje aplikacije u Docku
    public class DockItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsVisible { get; set; } = true;
    }

    // Klasa za spremanje svih postavki (ikone, visina, boja, startup i POZICIJA)
    public class DockConfig
    {
        public int DockHeight { get; set; } = 26;
        public string DockColor { get; set; } = "#40546A";
        public bool RunAtStartup { get; set; } = false;
        // Pohranjuje zadnji rub (ABE_TOP = 1 je default)
        public int DockEdge { get; set; } = 1;
        public List<DockItem> Applications { get; set; } = new List<DockItem>();
    }

    // Glavna klasa Dock aplikacije
    public class DockApp : Form
    {
        private const string RegistryAppName = "CustomDockApplication";

        // --- P/Invoke Konstante ---
        private const int ABM_NEW = 0x00000000;
        private const int ABM_REMOVE = 0x00000001;
        private const int ABM_QUERYPOS = 0x00000002;
        private const int ABM_SETPOS = 0x00000003;
        private const int ABM_ACTIVATE = 0x00000006;
        private const int ABM_WINDOWPOSCHANGED = 0x00000007;
        private const int ABE_LEFT = 0;
        private const int ABE_TOP = 1;
        private const int ABE_RIGHT = 2;
        private const int ABE_BOTTOM = 3;
        private const int WM_ACTIVATE = 0x0006;

        private static int AppBarMsgID = RegisterWindowMessage("APPBAR_CALLBACK_MESSAGE");

        private int CurrentDockEdge = ABE_TOP;

        private readonly ContextMenuStrip contextMenu;
        private readonly ToolTip toolTip;
        private readonly DockConfig _config;
        private readonly BindingList<DockItem> _appsBindingList;

        private const string ConfigFileName = "dock_apps.json";
        private readonly string DataFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _reloadTimer;
        private readonly object _reloadLock = new object();


        // P/Invoke Strukture
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        private static extern int SHAppBarMessage(int dwMessage, ref APPBARDATA pData);
        [DllImport("user32.dll")]
        private static extern int RegisterWindowMessage(string lpString);

        public DockApp()
        {
            toolTip = new ToolTip();
            _config = new DockConfig();
            _appsBindingList = new BindingList<DockItem>();
            contextMenu = new ContextMenuStrip();

            LoadApplications();
            CurrentDockEdge = _config.DockEdge;

            // Postavke forme
            Text = "Custom Dock";
            try
            {
                BackColor = ColorTranslator.FromHtml(_config.DockColor);
            }
            catch
            {
                BackColor = Color.FromArgb(64, 84, 106);
            }
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;

            InitializeControls();
            InitializeContextMenu();
            RegisterBar(true); // Registriraj dock na učitanom rubu
            SetupFileWatcher();
        }

        // --- Pomoćne metode (DarkenColor, IsColorLight, ManageStartupEntry) ---

        private Color DarkenColor(Color color, int amount)
        {
            int r = Math.Max(0, color.R - amount);
            int g = Math.Max(0, color.G - amount);
            int b = Math.Max(0, color.B - amount);
            return Color.FromArgb(color.A, r, g, b);
        }

        private bool IsColorLight(Color color)
        {
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            return luminance > 0.5;
        }

        public void ManageStartupEntry(bool enable)
        {
            RegistryKey? rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

            if (rk == null)
            {
                Debug.WriteLine("Neuspješno otvaranje Registry ključa za Startup.");
                return;
            }

            try
            {
                if (enable)
                {
                    rk.SetValue(RegistryAppName, Application.ExecutablePath);
                }
                else
                {
                    if (rk.GetValue(RegistryAppName) != null)
                    {
                        rk.DeleteValue(RegistryAppName);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Greška pri promjeni postavke za automatsko pokretanje: {ex.Message}", "Registry Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                rk.Close();
            }
        }

        // --- Logika Promatrača Datoteka (File Watcher) ---

        private void SetupFileWatcher()
        {
            string? dirPath = Path.GetDirectoryName(DataFilePath);

            if (dirPath == null)
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(dirPath, ConfigFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnConfigChanged;
                this.Disposed += (sender, e) => _watcher?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Greška pri postavljanju FileSystemWatcher-a: {ex.Message}");
            }
        }

        private void OnConfigChanged(object sender, FileSystemEventArgs e)
        {
            lock (_reloadLock)
            {
                _reloadTimer?.Dispose();
                _reloadTimer = new System.Threading.Timer(
                    (state) => {
                        this.Invoke((MethodInvoker)delegate {
                            ReloadApplications(DataFilePath);
                        });
                    },
                    null,
                    500,
                    System.Threading.Timeout.Infinite);
            }
        }

        // --- Logika Spremanja/Učitavanja ---

        private void LoadApplications()
        {
            ReloadApplications(DataFilePath);
        }

        private void ReloadApplications(string filePath)
        {
            string? jsonString = null;
            const int maxRetries = 5;
            const int delayMs = 100;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        jsonString = File.ReadAllText(filePath);
                        break;
                    }
                    else
                    {
                        if (i == 0) InitializeDefaultApplications();
                        return;
                    }
                }
                catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                {
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Greška pri učitavanju JSON-a: {ex.Message}");
                    return;
                }
            }

            if (jsonString != null)
            {
                LoadApplicationsLogic(jsonString);
                CurrentDockEdge = _config.DockEdge;
                InitializeControls();
            }
        }

        private void LoadApplicationsLogic(string jsonString)
        {
            _config.Applications.Clear();
            _appsBindingList.Clear();

            try
            {
                var loadedConfig = JsonSerializer.Deserialize<DockConfig>(jsonString);

                if (loadedConfig != null && loadedConfig.Applications != null)
                {
                    _config.DockHeight = loadedConfig.DockHeight;
                    _config.DockColor = loadedConfig.DockColor;
                    _config.RunAtStartup = loadedConfig.RunAtStartup;
                    _config.DockEdge = loadedConfig.DockEdge;

                    foreach (var item in loadedConfig.Applications)
                    {
                        _config.Applications.Add(item);
                        _appsBindingList.Add(item);
                    }
                    if (_config.DockHeight < 24) _config.DockHeight = 24;
                }
                else
                {
                    var legacyList = JsonSerializer.Deserialize<List<DockItem>>(jsonString);
                    if (legacyList != null)
                    {
                        foreach (var item in legacyList)
                        {
                            item.IsVisible = true;
                            _config.Applications.Add(item);
                            _appsBindingList.Add(item);
                        }
                        SaveApplications();
                    }
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"JSON Deserialization Error: {ex.Message}");
                InitializeDefaultApplications();
                SaveApplications();
            }
        }

        private void InitializeDefaultApplications()
        {
            _config.Applications.Clear();
            _appsBindingList.Clear();
            var initialApps = new List<DockItem>
            {
                new DockItem { Name = "Notepad", Path = "notepad.exe", IsVisible = true },
                new DockItem { Name = "WordPad (Write)", Path = "write.exe", IsVisible = true }
            };

            foreach (var item in initialApps)
            {
                _config.Applications.Add(item);
                _appsBindingList.Add(item);
            }
        }

        public void SaveApplications()
        {
            try
            {
                _config.Applications.Clear();
                // Spremi trenutnu poziciju (rub) u konfiguracijski objekt
                _config.DockEdge = CurrentDockEdge;

                foreach (var item in _appsBindingList)
                {
                    _config.Applications.Add(item);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(_config, options);

                File.WriteAllText(DataFilePath, jsonString);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Greška pri spremanju konfiguracije: {ex.Message}", "Greška pri spremanju", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --- Inicijalizacija Kontrola i Izbornika ---

        public void InitializeControls()
        {
            Controls.Clear();

            // Postavi boju docka prema konfiguraciji
            Color dockBackgroundColor;
            try
            {
                dockBackgroundColor = ColorTranslator.FromHtml(_config.DockColor);
                this.BackColor = dockBackgroundColor;
            }
            catch
            {
                dockBackgroundColor = Color.FromArgb(64, 84, 106);
                this.BackColor = dockBackgroundColor;
            }

            Color buttonVisualColor = dockBackgroundColor;
            Color buttonHoverColor = DarkenColor(dockBackgroundColor, 15);
            Color buttonMouseDownColor = DarkenColor(dockBackgroundColor, 30);


            int offset = 4;
            int safeDockHeight = _config.DockHeight > 24 ? _config.DockHeight : 40;
            int itemSize = safeDockHeight - 2;
            int padding = 6;
            bool isVertical = (CurrentDockEdge == ABE_LEFT || CurrentDockEdge == ABE_RIGHT);

            if (itemSize < 22) itemSize = 22;


            var orderedApps = _appsBindingList
                .Where(app => app.IsVisible)
                .ToList();

            // Dodavanje aplikacija kao gumba s ikonama
            foreach (var app in orderedApps)
            {

                Button iconButton = new Button
                {
                    Tag = app.Path,
                    Size = new Size(itemSize, itemSize),
                    BackColor = buttonVisualColor,
                    FlatStyle = FlatStyle.Flat
                };

                // Postavljanje pozicije na temelju orijentacije
                if (isVertical)
                {
                    // Dock LEFT/RIGHT: X je fiksni, Y se pomiče
                    iconButton.Location = new Point(1, offset);
                }
                else
                {
                    // Dock TOP/BOTTOM: X se pomiče, Y je fiksni
                    iconButton.Location = new Point(offset, 1);
                }

                iconButton.FlatAppearance.BorderSize = 0;
                iconButton.FlatAppearance.MouseDownBackColor = buttonMouseDownColor;
                iconButton.FlatAppearance.MouseOverBackColor = buttonHoverColor;


                toolTip.SetToolTip(iconButton, app.Name);

                // Izvlačenje ikone
                if (!string.IsNullOrEmpty(app.Path) && File.Exists(app.Path))
                {
                    try
                    {
                        using (Icon? appIcon = Icon.ExtractAssociatedIcon(app.Path))
                        {
                            if (appIcon != null)
                            {
                                int iconDim = itemSize - 8;
                                if (iconDim < 16) iconDim = 16;
                                iconButton.Image = new Bitmap(appIcon.ToBitmap(), new Size(iconDim, iconDim));
                                iconButton.ImageAlign = ContentAlignment.MiddleCenter;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        iconButton.Text = app.Name.Length >= 2 ? app.Name.Substring(0, 2) : app.Name;
                        iconButton.ForeColor = IsColorLight(dockBackgroundColor) ? Color.Black : Color.White;
                        Debug.WriteLine($"Greška pri učitavanju ikone za {app.Name}: {ex.Message}");
                    }
                }
                else
                {
                    iconButton.Text = app.Name.Length >= 2 ? app.Name.Substring(0, 2) : app.Name;
                    iconButton.ForeColor = IsColorLight(dockBackgroundColor) ? Color.Black : Color.White;
                }

                iconButton.Click += IconClick;
                Controls.Add(iconButton);
                offset += itemSize + padding;
            }


            // Postavljanje veličine Docka na temelju orijentacije
            int finalDimension = offset + 4;

            if (isVertical)
            {
                this.Width = safeDockHeight;
                this.Height = finalDimension;
            }
            else
            {
                this.Width = finalDimension;
                this.Height = safeDockHeight;
            }

            RepositionBar();
        }

        private void InitializeContextMenu()
        {
            var settingsMenuItem = new ToolStripMenuItem("Settings");
            settingsMenuItem.Click += (sender, e) => ShowSettings();

            var positionMenuItem = new ToolStripMenuItem("Position");

            var topMenuItem = new ToolStripMenuItem("Top");
            topMenuItem.Click += (sender, e) => SetDockEdge(ABE_TOP);

            var bottomMenuItem = new ToolStripMenuItem("Bottom");
            bottomMenuItem.Click += (sender, e) => SetDockEdge(ABE_BOTTOM);

            var leftMenuItem = new ToolStripMenuItem("Left");
            leftMenuItem.Click += (sender, e) => SetDockEdge(ABE_LEFT);

            var rightMenuItem = new ToolStripMenuItem("Right");
            rightMenuItem.Click += (sender, e) => SetDockEdge(ABE_RIGHT);

            positionMenuItem.DropDownItems.AddRange(new ToolStripItem[]
            {
                topMenuItem, bottomMenuItem, leftMenuItem, rightMenuItem
            });

            var exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += (sender, e) => Close();

            contextMenu.Items.AddRange(new ToolStripItem[]
            {
                settingsMenuItem,
                new ToolStripSeparator(),
                positionMenuItem,
                new ToolStripSeparator(),
                exitMenuItem
            });

            this.ContextMenuStrip = contextMenu;
        }

        private void ShowSettings()
        {
            DockSettingsForm settingsForm = new DockSettingsForm(_config, this, _appsBindingList);
            settingsForm.ShowDialog();
        }

        private void IconClick(object? sender, EventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                try
                {
                    Process.Start(path);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Greška pri pokretanju aplikacije: {path}. Detalji: {ex.Message}", "Greška", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // --- Metoda za izračunavanje offseta za dno ekrana (Taskbar visina) ---
        private int GetBottomOffset()
        {
            // Build broj verzije Windowsa. Windows 11 počinje s build brojem >= 22000.
            int build = Environment.OSVersion.Version.Build;

            // Windows 11 Taskbar je visok 48px
            if (build >= 22000)
            {
                return 48;
            }
            // Windows 10 Taskbar je visok 40px
            else
            {
                return 40;
            }
        }

        // --- AppBar i Pozicioniranje ---

        private void SetDockEdge(int edge)
        {
            // Prvo de-registriraj stari rub
            RegisterBar(false);

            CurrentDockEdge = edge;
            _config.DockEdge = edge;

            InitializeControls(); // Osvježava veličinu i poziva RepositionBar
            RegisterBar(true); // Ponovno registrira na novom rubu

            SaveApplications();
        }

        private void RegisterBar(bool register)
        {
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            abd.hWnd = Handle;

            if (register)
            {
                // Uvijek se prvo ukloni (u slučaju da je već negdje registriran)
                SHAppBarMessage(ABM_REMOVE, ref abd);

                abd.uCallbackMessage = AppBarMsgID;
                SHAppBarMessage(ABM_NEW, ref abd);

                // FIX: Dodajemo kratku sinkronizacijsku odgodu da Windows Taskbar reagira
                System.Threading.Thread.Sleep(50);

                RepositionBar();
            }
            else
            {
                SHAppBarMessage(ABM_REMOVE, ref abd);
            }
        }

        private void RepositionBar()
        {
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            abd.hWnd = Handle;
            abd.uEdge = CurrentDockEdge;

            int formW = this.Width;
            int formH = this.Height;
            int screenW = Screen.PrimaryScreen.Bounds.Width;
            int screenH = Screen.PrimaryScreen.Bounds.Height;

            // Postavljanje inicijalne RECT strukture prije upita sustavu (QUERYPOS)
            switch (CurrentDockEdge)
            {
                case ABE_LEFT:
                    abd.rc.left = 0; abd.rc.top = 0; abd.rc.right = formW; abd.rc.bottom = screenH;
                    break;
                case ABE_RIGHT:
                    abd.rc.left = screenW - formW; abd.rc.top = 0; abd.rc.right = screenW; abd.rc.bottom = screenH;
                    break;
                case ABE_TOP:
                    abd.rc.left = 0; abd.rc.top = 0; abd.rc.right = screenW; abd.rc.bottom = formH;
                    break;
                case ABE_BOTTOM:
                    // Primjena OS-ovisnog offseta na donjem rubu ekrana
                    int osOffset = GetBottomOffset();

                    // Postavlja gornji rub docka na (VisinaEkrana - VisinaDocka - TaskbarVisina)
                    abd.rc.left = 0;
                    abd.rc.top = screenH - formH - osOffset;

                    abd.rc.right = screenW;

                    // Postavlja donji rub docka na (VisinaEkrana - TaskbarVisina)
                    abd.rc.bottom = screenH - osOffset;
                    break;
            }

            // 1. Upit sustavu za optimalnu poziciju (QUERYPOS)
            SHAppBarMessage(ABM_QUERYPOS, ref abd);

            // 2. Trajno postavi poziciju (SETPOS)
            SHAppBarMessage(ABM_SETPOS, ref abd);

            // 3. Stvarno postavljanje prozora na poziciju koju je sustav odobrio
            Bounds = new Rectangle(
                abd.rc.left,
                abd.rc.top,
                abd.rc.right - abd.rc.left,
                abd.rc.bottom - abd.rc.top);

            // 4. Obavijestite sustav da se pozicija prozora promijenila
            SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == AppBarMsgID)
            {
                if (m.WParam.ToInt32() == 0x00000001) // ABN_POSCHANGED
                {
                    RepositionBar();
                }
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WM_ACTIVATE)
            {
                APPBARDATA abd = new APPBARDATA();
                abd.cbSize = Marshal.SizeOf(abd);
                abd.hWnd = Handle;
                SHAppBarMessage(ABM_ACTIVATE, ref abd);
            }

            base.WndProc(ref m);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveApplications();
            RegisterBar(false);
            _reloadTimer?.Dispose();
            _watcher?.Dispose();
            base.OnClosing(e);
        }
    }

    // Klasa za postavljanje postavki (DockSettingsForm)
    public class DockSettingsForm : Form
    {
        private readonly DockConfig _config;
        private readonly DockApp _parentDock;
        private readonly BindingList<DockItem> _apps;

        private DataGridView dgvApps = null!;
        private NumericUpDown numHeight = null!;
        private Button btnSave = null!;
        private Button btnClose = null!;
        private Button btnUp = null!;
        private Button btnDown = null!;
        private Button btnDelete = null!; // Novi gumb za brisanje

        private ColorDialog colorDialog = null!;
        private Panel pnlColorPreview = null!;
        private CheckBox chkStartup = null!;
        private Color selectedColor;


        public DockSettingsForm(DockConfig config, DockApp parentDock, BindingList<DockItem> apps)
        {
            _config = config;
            _parentDock = parentDock;
            _apps = apps;

            try
            {
                selectedColor = ColorTranslator.FromHtml(_config.DockColor);
            }
            catch
            {
                selectedColor = Color.FromArgb(64, 84, 106);
            }

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Dock Settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 700;
            Height = 470;

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(10),
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));


            // --- Row 0: Dock Background Color ---
            colorDialog = new ColorDialog();

            Label lblColor = new Label { Text = "Dock Background Color:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

            TableLayoutPanel colorPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            colorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            colorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            pnlColorPreview = new Panel
            {
                BackColor = selectedColor,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(3)
            };

            Button btnPickColor = new Button { Text = "Pick Color", Dock = DockStyle.Fill };
            btnPickColor.Click += BtnPickColor_Click;

            colorPanel.Controls.Add(pnlColorPreview, 0, 0);
            colorPanel.Controls.Add(btnPickColor, 1, 0);

            mainLayout.Controls.Add(lblColor, 0, 0);
            mainLayout.Controls.Add(colorPanel, 1, 0);


            // --- Row 1: Dock Height ---
            Label lblHeight = new Label { Text = "Dock Height (px):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            numHeight = new NumericUpDown
            {
                Minimum = 26,
                Maximum = 100,
                Value = _config.DockHeight,
                Dock = DockStyle.Fill
            };

            mainLayout.Controls.Add(lblHeight, 0, 1);
            mainLayout.Controls.Add(numHeight, 1, 1);

            // --- Row 2: Run at Startup ---
            chkStartup = new CheckBox
            {
                Text = "Run at Windows Startup",
                Checked = _config.RunAtStartup,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 0, 3, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            mainLayout.Controls.Add(chkStartup, 0, 2);
            mainLayout.SetColumnSpan(chkStartup, 2);

            // --- Row 3: Application Grid and Reorder Buttons ---

            TableLayoutPanel gridPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
            };
            gridPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            gridPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));

            dgvApps = new DataGridView
            {
                AutoGenerateColumns = true,
                DataSource = _apps,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                Dock = DockStyle.Fill,
                EditMode = DataGridViewEditMode.EditOnEnter,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };

            dgvApps.DataBindingComplete += (s, e) =>
            {
                if (dgvApps.Columns.Count > 0)
                {
                    dgvApps.Columns["IsVisible"]!.HeaderText = "Show";
                    dgvApps.Columns["IsVisible"]!.DisplayIndex = 0;
                    dgvApps.Columns["IsVisible"]!.Width = 50;
                    dgvApps.Columns["IsVisible"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;

                    dgvApps.Columns["Name"]!.HeaderText = "Application Name";
                    dgvApps.Columns["Path"]!.HeaderText = "Executable Path";

                    if (dgvApps.Columns["BrowseColumn"] == null)
                    {
                        DataGridViewButtonColumn browseCol = new DataGridViewButtonColumn();
                        browseCol.Name = "BrowseColumn";
                        browseCol.HeaderText = "Browse";
                        browseCol.Text = "Browse";
                        browseCol.UseColumnTextForButtonValue = true;
                        dgvApps.Columns.Add(browseCol);
                    }

                    dgvApps.SelectionChanged += DgvApps_SelectionChanged;

                    DgvApps_SelectionChanged(dgvApps, EventArgs.Empty);
                }
            };

            dgvApps.CellContentClick += DgvApps_CellContentClick;

            // Reorder Panel prilagođen za gumb Delete
            TableLayoutPanel reorderPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                RowStyles = {
                    new RowStyle(SizeType.Absolute, 35F), // Up
                    new RowStyle(SizeType.Absolute, 35F), // Down
                    new RowStyle(SizeType.Absolute, 10F), // Separator
                    new RowStyle(SizeType.Absolute, 35F), // Delete
                    new RowStyle(SizeType.Percent, 100F), // Spacer
                }
            };

            Font arrowFont = new Font(this.Font.FontFamily, 12, FontStyle.Bold);

            btnUp = new Button
            {
                Text = "↑",
                Dock = DockStyle.Fill,
                Enabled = false,
                Font = arrowFont,
                Size = new Size(35, 35)
            };
            btnUp.Click += (s, e) => MoveItem(-1);

            btnDown = new Button
            {
                Text = "↓",
                Dock = DockStyle.Fill,
                Enabled = false,
                Font = arrowFont,
                Size = new Size(35, 35)
            };
            btnDown.Click += (s, e) => MoveItem(1);

            btnDelete = new Button
            { // Inicijalizacija gumba Delete
                Text = "Delete",
                Dock = DockStyle.Fill,
                Enabled = false,
                Size = new Size(35, 35)
            };
            btnDelete.Click += (s, e) => DeleteItem();

            reorderPanel.Controls.Add(btnUp, 0, 0);
            reorderPanel.Controls.Add(btnDown, 0, 1);
            reorderPanel.Controls.Add(btnDelete, 0, 3); // Dodavanje gumba Delete

            gridPanel.Controls.Add(dgvApps, 0, 0);
            gridPanel.Controls.Add(reorderPanel, 1, 0);

            mainLayout.Controls.Add(gridPanel, 0, 3);
            mainLayout.SetColumnSpan(gridPanel, 2);

            // --- Row 4: Buttons ---
            btnSave = new Button { Text = "Save and Apply", Dock = DockStyle.Fill };
            btnSave.Click += BtnSave_Click;

            btnClose = new Button { Text = "Close", Dock = DockStyle.Fill };
            btnClose.Click += (s, e) => Close();

            mainLayout.Controls.Add(btnSave, 0, 4);
            mainLayout.Controls.Add(btnClose, 1, 4);

            Controls.Add(mainLayout);
        }

        private void BtnPickColor_Click(object? sender, EventArgs e)
        {
            colorDialog.Color = selectedColor;
            if (colorDialog.ShowDialog() == DialogResult.OK)
            {
                selectedColor = colorDialog.Color;
                pnlColorPreview.BackColor = selectedColor;
            }
        }

        private void DgvApps_SelectionChanged(object? sender, EventArgs e)
        {
            if (dgvApps.SelectedRows.Count == 0 || _apps.Count == 0)
            {
                btnUp.Enabled = false;
                btnDown.Enabled = false;
                btnDelete.Enabled = false; // Ažuriranje stanja gumba Delete
                return;
            }

            int currentIndex = dgvApps.SelectedRows[0].Index;

            btnUp.Enabled = currentIndex > 0;
            btnDown.Enabled = currentIndex < _apps.Count - 1;
            btnDelete.Enabled = true; // Omogući brisanje kada je red odabran
        }

        private void MoveItem(int direction)
        {
            if (dgvApps.SelectedRows.Count == 0 || _apps.Count == 0) return;

            int currentIndex = dgvApps.SelectedRows[0].Index;
            int newIndex = currentIndex + direction;

            if (newIndex >= 0 && newIndex < _apps.Count)
            {
                DockItem itemToMove = _apps[currentIndex];

                _apps.RemoveAt(currentIndex);
                _apps.Insert(newIndex, itemToMove);

                dgvApps.Rows[newIndex].Selected = true;
                dgvApps.CurrentCell = dgvApps.Rows[newIndex].Cells[0];

                DgvApps_SelectionChanged(dgvApps, EventArgs.Empty);
            }
        }

        private void DeleteItem()
        {
            if (dgvApps.SelectedRows.Count == 0 || _apps.Count == 0) return;

            int currentIndex = dgvApps.SelectedRows[0].Index;
            if (currentIndex >= 0 && currentIndex < _apps.Count)
            {
                string appName = _apps[currentIndex].Name;

                DialogResult result = MessageBox.Show(
                    $"Are you sure you want to delete '{appName}'?",
                    "Confirm Delete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    _apps.RemoveAt(currentIndex);
                    // DgvApps_SelectionChanged se automatski poziva nakon promjene BindingList-a
                }
            }
        }

        private void DgvApps_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            if (dgvApps.Columns[e.ColumnIndex].Name == "BrowseColumn")
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*";
                    ofd.Title = "Select Executable File";

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        if (e.RowIndex < _apps.Count)
                        {
                            _apps[e.RowIndex].Path = ofd.FileName;
                            dgvApps.RefreshEdit();
                        }
                    }
                }
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            dgvApps.EndEdit();

            _config.DockHeight = (int)numHeight.Value;
            _config.DockColor = ColorTranslator.ToHtml(selectedColor);

            bool newStartupState = chkStartup.Checked;
            _config.RunAtStartup = newStartupState;

            _parentDock.SaveApplications();
            _parentDock.InitializeControls();

            _parentDock.ManageStartupEntry(newStartupState);

            MessageBox.Show("Settings saved and applied.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }
}
