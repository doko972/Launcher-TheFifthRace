// SGWLauncher — launcher de mise a jour du client Stargate Worlds.
//
// Le joueur installe le client d'origine, puis lance cet executable. Le launcher
// telecharge le manifeste publie sur le site, compare les hashes SHA-256 avec les
// fichiers locaux, recupere uniquement ce qui a change, puis demarre le jeu.
//
// L'interface est une page HTML rendue par WebView2. Les assemblies WebView2, la
// page et les polices sont embarquees dans l'executable : un seul fichier a
// distribuer. Voir UpdateEngine.cs pour la logique de mise a jour.
//
// Compilation : voir build.ps1 (csc.exe du .NET Framework 4.8, deja present sous Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SGWLauncher
{
    internal static class Program
    {
        // Comparee a manifest.launcher.version pour declencher l'auto-mise a jour.
        // Toute publication du launcher exige d'incrementer cette valeur : la
        // comparaison porte sur l'egalite des chaines, un binaire republie sous
        // la meme version ne serait distribue a personne.
        internal const string LauncherVersion = "2.1.0";
        internal const string DefaultUpdateUrl = "https://thefifthrace.online/client/manifest.json";

        // Hote virtuel servi depuis les ressources embarquees.
        internal const string UiOrigin = "https://sgw.local";

        internal static string ExePath;

        [STAThread]
        private static void Main()
        {
            ExePath = Assembly.GetExecutingAssembly().Location;

            // Les assemblies WebView2 sont embarquees : il faut les resoudre AVANT
            // que le moindre type WebView2 soit charge par le JIT.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

            // Reliquats d'une auto-mise a jour precedente : l'ancien binaire renomme
            // et le script de permutation. Ce dernier ne s'efface plus lui-meme --
            // un « del "%~f0" » en fin de script est un idiome de dropper -- c'est
            // donc au launcher de faire le menage a son demarrage suivant.
            try
            {
                string old = ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);

                string cmd = Path.Combine(LauncherConfig.Dir, "maj-launcher.cmd");
                if (File.Exists(cmd)) File.Delete(cmd);
            }
            catch { /* sans importance, on retentera au prochain lancement */ }

            // .NET 4.8 suit le systeme, mais on force TLS 1.2 au cas ou le poste soit ancien.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            NativeLoader.Extract();
            Run();
        }

        // Isole du JIT de Main : sans ca, les types WebView2 seraient resolus trop tot.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run()
        {
            if (!WebView2Runtime.EnsureAvailable()) return;
            Application.Run(new LauncherForm());
        }

        /// <summary>
        /// Resout les assemblies WebView2 embarquees en les ecrivant sur disque,
        /// puis en les chargeant par chemin.
        /// </summary>
        /// <remarks>
        /// La version precedente appelait Assembly.Load(byte[]) : le code s'executait
        /// sans jamais toucher le disque. C'est commode, mais c'est aussi la maniere
        /// dont un empaqueteur deballe sa charge utile, et les moteurs par
        /// apprentissage le classent comme tel -- VirusTotal etiquetait d'ailleurs le
        /// binaire « assembly / anomalous ».
        ///
        /// Les DLL sont donc extraites a cote du WebView2Loader natif, que
        /// NativeLoader ecrit deja au meme endroit et pour les memes raisons. Le
        /// fichier unique reste le mode de distribution : l'extraction a lieu au
        /// premier lancement, dans %LOCALAPPDATA% qui est toujours accessible en
        /// ecriture, meme si le jeu est installe dans Program Files.
        ///
        /// En cas d'echec d'ecriture (dossier verrouille, antivirus tiers), on
        /// retombe sur le chargement en memoire : mieux vaut un launcher qui demarre.
        /// </remarks>
        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            if (!simpleName.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase))
                return null;

            byte[] dll = Res.Bytes("lib/" + simpleName + ".dll");
            if (dll == null) return null;

            try
            {
                string dir = Path.Combine(LauncherConfig.Dir, "runtime");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, simpleName + ".dll");

                // Meme precaution que NativeLoader : ne reecrire que si absent ou
                // de taille differente, le fichier pouvant etre verrouille par une
                // instance deja lancee.
                if (!File.Exists(dest) || new FileInfo(dest).Length != dll.Length)
                {
                    try { File.WriteAllBytes(dest, dll); }
                    catch (IOException) { /* deja charge par un autre processus */ }
                }

                if (File.Exists(dest)) return Assembly.LoadFrom(dest);
            }
            catch { /* on bascule sur le chargement en memoire */ }

            return Assembly.Load(dll);
        }
    }

    // ------------------------------------------------------------------------
    // Ressources embarquees
    // ------------------------------------------------------------------------

    internal static class Res
    {
        public static Stream Open(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        public static byte[] Bytes(string name)
        {
            using (Stream s = Open(name))
            {
                if (s == null) return null;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
    }

    // WebView2Loader.dll est natif : il doit exister sur disque avant que
    // Microsoft.Web.WebView2.Core y fasse ses P/Invoke.
    internal static class NativeLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        public static void Extract()
        {
            try
            {
                string arch = IntPtr.Size == 8 ? "x64" : "x86";
                byte[] dll = Res.Bytes("lib/WebView2Loader." + arch + ".dll");
                if (dll == null) return;

                string dir = Path.Combine(LauncherConfig.Dir, "runtime", arch);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, "WebView2Loader.dll");

                // Ne reecrire que si absent ou de taille differente : le fichier
                // peut etre verrouille par une instance deja lancee.
                if (!File.Exists(dest) || new FileInfo(dest).Length != dll.Length)
                {
                    try { File.WriteAllBytes(dest, dll); }
                    catch (IOException) { /* deja charge par un autre processus */ }
                }

                // Charge par chemin complet : les P/Invoke ulterieurs sur le nom
                // court reutiliseront ce module deja en memoire.
                LoadLibrary(dest);
            }
            catch { /* on laissera EnsureAvailable diagnostiquer */ }
        }
    }

    // ------------------------------------------------------------------------
    // Runtime WebView2 (Evergreen)
    // ------------------------------------------------------------------------

    internal static class WebView2Runtime
    {
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // Retourne false si l'application doit s'arreter.
        public static bool EnsureAvailable()
        {
            if (IsInstalled()) return true;

            DialogResult r = MessageBox.Show(
                "This launcher needs the Microsoft Edge WebView2 component, which is missing\n" +
                "from this PC.\n\n" +
                "Install it now? (about 2 MB, downloaded from Microsoft)",
                "Missing component", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;

            try
            {
                // Dans le dossier du launcher plutot que dans %TEMP% : « telecharger
                // un executable dans le repertoire temporaire puis l'executer » est
                // le geste que les moteurs par apprentissage guettent en premier.
                // L'installeur est celui de Microsoft, telecharge depuis
                // go.microsoft.com et lance avec l'accord explicite du joueur ; rien
                // ne justifie de lui donner l'allure d'une charge utile deposee.
                Directory.CreateDirectory(LauncherConfig.Dir);
                string setup = Path.Combine(LauncherConfig.Dir, "MicrosoftEdgeWebview2Setup.exe");
                using (var wc = new WebClient()) wc.DownloadFile(BootstrapperUrl, setup);

                var psi = new ProcessStartInfo(setup, "/silent /install");
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi)) p.WaitForExit();

                try { File.Delete(setup); } catch { }

                if (IsInstalled())
                {
                    // Relancer : le runtime doit etre present des le demarrage du processus.
                    var restart = new ProcessStartInfo(Program.ExePath);
                    restart.UseShellExecute = true;
                    Process.Start(restart);
                    return false;
                }

                MessageBox.Show("The installation did not complete. Try again, or install " +
                                "\"Microsoft Edge WebView2 Runtime\" manually.",
                    "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Installation failed:\n\n" + ex.Message,
                    "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }

        private static bool IsInstalled()
        {
            try
            {
                string v = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrEmpty(v);
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------------
    // Fenetre hote
    // ------------------------------------------------------------------------

    internal sealed class LauncherForm : Form
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int value, int size);

        private readonly WebView2 _web = new WebView2();
        private readonly UpdateEngine _engine = new UpdateEngine();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private bool _uiReady;
        private bool _quitting;

        public LauncherForm()
        {
            Text = "The Fifth Race — Launcher";
            ClientSize = new Size(900, 620);   // laisse respirer l'illustration de fond
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(2, 8, 16);
            KeyPreview = true;

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.FromArgb(2, 8, 16);
            Controls.Add(_web);

            _engine.StateChanged += OnEngineState;
            _engine.GamePathNeeded += OnGamePathNeeded;
            _engine.ElevationNeeded += OnElevationNeeded;
            _engine.SelfUpdateStarted += OnSelfUpdateStarted;

            Load += OnLoaded;
            FormClosing += delegate { _quitting = true; _engine.Stop(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Coins arrondis Windows 11 : sans bordure native, il faut le demander.
            try
            {
                int pref = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* Windows 10 : sans effet */ }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        // -- Initialisation de WebView2 ---------------------------------------

        private async void OnLoaded(object sender, EventArgs e)
        {
            try
            {
                // Le dossier de donnees doit etre inscriptible : le jeu peut etre
                // installe dans Program Files.
                string userData = Path.Combine(LauncherConfig.Dir, "webview");
                Directory.CreateDirectory(userData);

                var opts = new CoreWebView2EnvironmentOptions();
                opts.AdditionalBrowserArguments = "--disable-features=msWebOOUI,msPdfOOUI";
                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(null, userData, opts);
                await _web.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("SGW_DEVTOOLS") == "1";
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;

                core.WebMessageReceived += OnWebMessage;
                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs a)
                {
                    a.Handled = true;   // rien ne doit s'ouvrir en dehors du launcher
                };

                // L'interface est servie depuis les ressources embarquees.
                core.AddWebResourceRequestedFilter(Program.UiOrigin + "/*",
                    CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnWebResourceRequested;

                core.Navigate(Program.UiOrigin + "/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "The interface could not start:\n\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string path;
            try { path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'); }
            catch { return; }
            if (path.Length == 0) path = "index.html";

            byte[] data = Res.Bytes("ui/" + path);
            if (data == null)
            {
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "");
                return;
            }

            string headers = "Content-Type: " + MimeOf(path) +
                             "\r\nCache-Control: no-store";
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(data), 200, "OK", headers);
        }

        private static string MimeOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".html") return "text/html; charset=utf-8";
            if (ext == ".css") return "text/css; charset=utf-8";
            if (ext == ".js") return "application/javascript; charset=utf-8";
            if (ext == ".woff2") return "font/woff2";
            if (ext == ".svg") return "image/svg+xml";
            if (ext == ".png") return "image/png";
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            return "application/octet-stream";
        }

        // -- Messages venus de la page ----------------------------------------

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrEmpty(raw)) return;

            Dictionary<string, object> msg;
            try { msg = _json.DeserializeObject(raw) as Dictionary<string, object>; }
            catch { return; }
            if (msg == null) return;

            object cmdObj;
            if (!msg.TryGetValue("cmd", out cmdObj)) return;
            string cmd = Convert.ToString(cmdObj);
            object val;
            msg.TryGetValue("value", out val);

            switch (cmd)
            {
                case "ready":
                    _uiReady = true;
                    Push();                  // etat initial
                    _engine.StartCheck();
                    break;

                case "play":
                    string err = _engine.Play();
                    if (err != null)
                        MessageBox.Show(this, err, "Cannot start the game",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else
                        Close();
                    break;

                case "shard":
                    _engine.SetShard(Convert.ToString(val));
                    break;

                case "dev":
                    _engine.SetDevMode(Convert.ToBoolean(val));
                    break;

                case "changeDir":
                    if (PromptForGamePath()) _engine.StartCheck();
                    break;

                case "retry":
                    _engine.StartCheck();
                    break;

                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;

                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;

                case "close":
                    Close();
                    break;
            }
        }

        // -- Etat du moteur -> page -------------------------------------------

        private void OnEngineState(EngineState state)
        {
            if (_quitting || !IsHandleCreated) return;
            try { BeginInvoke((Action)Push); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Push()
        {
            if (!_uiReady || _quitting || _web.CoreWebView2 == null) return;
            try
            {
                string json = _json.Serialize(_engine.State);
                _web.CoreWebView2.ExecuteScriptAsync("window.sgw.setState(" + json + ")");
            }
            catch { /* page en cours de rechargement */ }
        }

        // -- Boites de dialogue natives ----------------------------------------

        private bool PromptForGamePath()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select your Stargate Worlds installation folder " +
                                  "(the one containing the \"Working\" folder).";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;

                if (!GameLocator.IsGameRoot(dlg.SelectedPath))
                {
                    MessageBox.Show(this,
                        "This folder does not contain Working\\binaries\\SGW.exe.\n\n" +
                        "Pick the game's root folder, not a sub-folder.",
                        "Invalid folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                _engine.SetGamePath(dlg.SelectedPath);
                return true;
            }
        }

        private void OnGamePathNeeded()
        {
            if (_quitting || !IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                MessageBox.Show(this,
                    "The launcher could not find your Stargate Worlds installation.\n\n" +
                    "Please select the game folder (the one containing \"Working\").",
                    "Game not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (PromptForGamePath()) _engine.StartCheck();
            });
        }

        private void OnElevationNeeded()
        {
            if (_quitting || !IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                if (UpdateEngine.IsElevated())
                {
                    MessageBox.Show(this,
                        "The game folder is not writable, even as administrator:\n\n" +
                        _engine.Config.GamePath,
                        "Cannot update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DialogResult r = MessageBox.Show(this,
                    "The game is installed in a folder protected by Windows.\n\n" +
                    "The launcher will restart as administrator to apply the update.",
                    "Elevation required", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (r != DialogResult.OK) return;

                try
                {
                    var psi = new ProcessStartInfo(Program.ExePath);
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    Close();
                }
                catch { /* UAC refuse par l'utilisateur */ }
            });
        }

        private void OnSelfUpdateStarted()
        {
            if (_quitting || !IsHandleCreated) return;
            BeginInvoke((Action)Close);
        }
    }
}
