// Moteur de mise a jour du client — aucune dependance a l'interface.
//
// L'hote (SGWLauncher.cs) s'abonne a StateChanged et retransmet l'etat a la page
// WebView2. Tout le travail reseau et disque se fait sur un thread de fond.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace SGWLauncher
{
    // ------------------------------------------------------------------------
    // Modele
    // ------------------------------------------------------------------------

    internal sealed class FileEntry
    {
        public string Path;      // relatif a la racine du jeu, separateurs '/'
        public string Sha256;
        public long Size;
        public string Channel;   // "player" ou "editor"
        public string Url;       // absolue, resolue depuis baseUrl
    }

    internal sealed class Shard
    {
        public string name;
        public string arg;       // valeur passee a SGW.exe -s
    }

    internal sealed class Manifest
    {
        public string Version = "";
        public List<FileEntry> Files = new List<FileEntry>();
        public List<string> Remove = new List<string>();
        public List<Shard> Shards = new List<Shard>();
        public string LauncherVersion;
        public string LauncherUrl;
        public string LauncherSha256;
        public string SplashUrl;   // fond impose par le manifeste (optionnel)
    }

    // Etat pousse tel quel vers JavaScript (champs en minuscules = cles JSON).
    internal sealed class EngineState
    {
        public string phase = "init";   // init|checking|scanning|downloading|ready|error|offline|needpath
        public string status = "";
        public string detail = "";
        public double progress;
        public bool canPlay;
        public bool busy = true;
        public List<Shard> shards = new List<Shard>();
        public string shard = "";
        public bool devMode;
        public string gamePath = "";
        public string version = "";
        public string launcherVersion = Program.LauncherVersion;
        public long bytesDone;
        public long bytesTotal;
        public int filesTodo;
        public string splashUrl = "";   // vide = image de fond embarquee
    }

    // ------------------------------------------------------------------------
    // Configuration locale
    // ------------------------------------------------------------------------

    internal sealed class LauncherConfig
    {
        // Dans %LOCALAPPDATA% et non a cote de l'executable : le jeu peut etre
        // installe dans Program Files, ou l'ecriture demande une elevation.
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SGWLauncher");

        private static readonly string File_ = Path.Combine(Dir, "SGWLauncher.ini");

        public string GamePath = "";
        public string Shard = "";
        public bool DevMode;
        public string UpdateUrl = Program.DefaultUpdateUrl;

        // Auto-mise a jour du launcher. Vrai chez les joueurs ; se met a 0 dans le
        // .ini sur un poste de developpement, ou un binaire tout juste compile se
        // ferait sinon ecraser par la version publiee des son premier lancement.
        public bool SelfUpdate = true;

        public static LauncherConfig Load()
        {
            var c = new LauncherConfig();
            try { Directory.CreateDirectory(Dir); } catch { }
            if (!File.Exists(File_)) return c;
            try
            {
                foreach (string raw in File.ReadAllLines(File_))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "gamepath") c.GamePath = val;
                    else if (key == "shard") c.Shard = val;
                    else if (key == "devmode") c.DevMode = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "updateurl" && val.Length > 0) c.UpdateUrl = val;
                    // Seul un « 0 » explicite desactive l'auto-mise a jour : une
                    // cle absente ou illisible doit laisser les joueurs a jour.
                    else if (key == "selfupdate") c.SelfUpdate = !(val == "0" || val.ToLowerInvariant() == "false");
                }
            }
            catch { /* fichier illisible : valeurs par defaut */ }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# The Fifth Race launcher configuration - generated automatically.");
                sb.AppendLine("GamePath=" + GamePath);
                sb.AppendLine("Shard=" + Shard);
                sb.AppendLine("DevMode=" + (DevMode ? "1" : "0"));
                sb.AppendLine("UpdateUrl=" + UpdateUrl);
                sb.AppendLine("SelfUpdate=" + (SelfUpdate ? "1" : "0"));
                File.WriteAllText(File_, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* on perd juste la memorisation des choix */ }
        }
    }

    // ------------------------------------------------------------------------
    // Localisation du client
    // ------------------------------------------------------------------------

    internal static class GameLocator
    {
        // Un dossier est la racine du jeu s'il contient Working\binaries\SGW.exe.
        public static bool IsGameRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try { return File.Exists(Path.Combine(dir, @"Working\binaries\SGW.exe")); }
            catch { return false; }
        }

        public static string Detect(string exePath)
        {
            // 1. Le launcher est pose dans l'arborescence du jeu.
            string dir = Path.GetDirectoryName(exePath);
            for (int i = 0; i < 4 && dir != null; i++)
            {
                if (IsGameRoot(dir)) return dir;
                dir = Path.GetDirectoryName(dir);
            }

            // 2. Entree de desinstallation laissee par l'installeur du client.
            string fromReg = FromRegistry();
            if (fromReg != null) return fromReg;

            // 3. Emplacements habituels.
            string[] common =
            {
                @"C:\Program Files (x86)\FireSky\Stargate Worlds-QA",
                @"C:\Program Files\FireSky\Stargate Worlds-QA",
                @"C:\SGW\Stargate Worlds-QA",
            };
            foreach (string c in common)
                if (IsGameRoot(c)) return c;

            return null;
        }

        private static string FromRegistry()
        {
            RegistryView[] views = { RegistryView.Registry32, RegistryView.Registry64 };
            foreach (RegistryView view in views)
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey uninstall = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                        {
                            using (RegistryKey app = uninstall.OpenSubKey(name))
                            {
                                if (app == null) continue;
                                string display = app.GetValue("DisplayName") as string;
                                if (display == null || display.IndexOf("Stargate Worlds",
                                        StringComparison.OrdinalIgnoreCase) < 0) continue;

                                string loc = app.GetValue("InstallLocation") as string;
                                if (IsGameRoot(loc)) return loc;

                                // InstallLocation est souvent vide : le chemin de
                                // l'uninstaller pointe alors sur la racine du jeu.
                                string uninst = app.GetValue("UninstallString") as string;
                                if (!string.IsNullOrEmpty(uninst))
                                {
                                    try
                                    {
                                        string d = Path.GetDirectoryName(uninst.Trim('"'));
                                        if (IsGameRoot(d)) return d;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { /* cle absente ou droits insuffisants */ }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------------
    // Moteur
    // ------------------------------------------------------------------------

    internal sealed class UpdateEngine
    {
        public readonly LauncherConfig Config = LauncherConfig.Load();
        public readonly EngineState State = new EngineState();

        public event Action<EngineState> StateChanged;
        public event Action GamePathNeeded;      // aucune installation trouvee
        public event Action ElevationNeeded;     // dossier du jeu en lecture seule
        public event Action SelfUpdateStarted;   // l'hote doit fermer l'application

        private Manifest _manifest;
        private volatile bool _stop;
        private Thread _worker;

        public UpdateEngine()
        {
            State.shard = Config.Shard;
            State.devMode = Config.DevMode;
            State.gamePath = Config.GamePath;
            State.shards = DefaultShards();
        }

        private static List<Shard> DefaultShards()
        {
            // Repli sur les cibles des .bat livres avec le client.
            var l = new List<Shard>();
            l.Add(new Shard { name = "Production (live)", arg = "PRODLIVE" });
            l.Add(new Shard { name = "Test", arg = "PRODTEST" });
            return l;
        }

        public void Stop() { _stop = true; }

        // -- Etat -------------------------------------------------------------

        private void Emit()
        {
            Action<EngineState> h = StateChanged;
            if (h != null) h(State);
        }

        private void Set(string phase, string status, string detail)
        {
            State.phase = phase;
            State.status = status;
            State.detail = detail;
            Emit();
        }

        // -- Commandes venues de l'interface ----------------------------------

        public void SetShard(string arg)
        {
            State.shard = arg;
            Config.Shard = arg;
            Config.Save();
        }

        public void SetDevMode(bool on)
        {
            State.devMode = on;
            Config.DevMode = on;
            Config.Save();
            StartCheck();   // le canal change : on reevalue le lot
        }

        public void SetGamePath(string path)
        {
            Config.GamePath = path;
            State.gamePath = path;
            Config.Save();
        }

        public void StartCheck()
        {
            if (_worker != null && _worker.IsAlive) return;
            State.canPlay = false;
            State.busy = true;
            State.progress = 0;
            State.bytesDone = 0;
            State.bytesTotal = 0;
            State.filesTodo = 0;
            _worker = new Thread(Run);
            _worker.IsBackground = true;
            _worker.Start();
        }

        // -- Boucle principale -------------------------------------------------

        private void Run()
        {
            try
            {
                // 1. Localiser le client.
                if (!GameLocator.IsGameRoot(Config.GamePath))
                {
                    Set("checking", "Recherche de l'installation du jeu...", "");
                    string found = GameLocator.Detect(Program.ExePath);
                    if (found == null)
                    {
                        State.busy = false;
                        Set("needpath", "Installation du jeu introuvable.",
                            "Select the folder that contains \"Working\".");
                        Action h = GamePathNeeded;
                        if (h != null) h();
                        return;
                    }
                    SetGamePath(found);
                }

                // 2. Recuperer le manifeste.
                Set("checking", "Checking for updates\u2026", "");
                try
                {
                    _manifest = FetchManifest(Config.UpdateUrl);
                }
                catch (Exception ex)
                {
                    // Hors ligne : on laisse jouer avec ce qui est installe.
                    State.busy = false;
                    State.canPlay = true;
                    State.progress = 0;
                    Set("offline", "Update server unreachable.",
                        "The game can still start with the files already installed. (" + ex.Message + ")");
                    return;
                }

                if (_manifest.Shards.Count > 0) State.shards = _manifest.Shards;
                State.version = _manifest.Version;
                if (!string.IsNullOrEmpty(_manifest.SplashUrl)) State.splashUrl = _manifest.SplashUrl;
                if (string.IsNullOrEmpty(State.shard) && State.shards.Count > 0)
                    State.shard = State.shards[0].arg;

                // 3. Le launcher lui-meme est-il a jour ?
                //
                // La comparaison porte sur l'INEGALITE et non sur « le manifeste
                // est-il plus recent » : republier une version anterieure ramene
                // donc tout le monde dessus, ce qui est le seul moyen d'annuler
                // une livraison ratee.
                //
                // Revers de la medaille sur un poste de developpement : lancer un
                // binaire fraichement compile, dont la version depasse celle du
                // manifeste, le fait se remplacer par la version en ligne. D'ou
                // SelfUpdate=0 dans le .ini, qui neutralise ce bloc en local.
                if (Config.SelfUpdate &&
                    !string.IsNullOrEmpty(_manifest.LauncherVersion) &&
                    _manifest.LauncherVersion != Program.LauncherVersion &&
                    !string.IsNullOrEmpty(_manifest.LauncherUrl))
                {
                    Set("downloading", "Updating the launcher (v" + _manifest.LauncherVersion + ")\u2026", "");
                    if (SelfUpdate(_manifest.LauncherUrl, _manifest.LauncherSha256))
                    {
                        Action h = SelfUpdateStarted;
                        if (h != null) h();
                        return;
                    }
                    // Echec : on poursuit, le client peut quand meme etre mis a jour.
                }

                // 4. Comparer les fichiers.
                var wanted = new List<FileEntry>();
                foreach (FileEntry f in _manifest.Files)
                    if (Config.DevMode || f.Channel != "editor") wanted.Add(f);

                var todo = new List<FileEntry>();
                long totalBytes = 0;
                int scanned = 0;

                foreach (FileEntry f in wanted)
                {
                    if (_stop) return;
                    scanned++;
                    State.progress = (double)scanned / Math.Max(1, wanted.Count);
                    Set("scanning", "Checking client files\u2026",
                        scanned + " / " + wanted.Count + "  —  " + ShortName(f.Path));

                    string local = Path.Combine(Config.GamePath, f.Path.Replace('/', '\\'));
                    if (!File.Exists(local) || !HashEquals(local, f.Sha256))
                    {
                        todo.Add(f);
                        totalBytes += f.Size;
                    }
                }

                // 5. Supprimer les fichiers retires par une version anterieure.
                foreach (string rel in _manifest.Remove)
                {
                    if (!IsSafeRelativePath(rel)) continue;
                    string local = Path.Combine(Config.GamePath, rel.Replace('/', '\\'));
                    try { if (File.Exists(local)) File.Delete(local); }
                    catch { /* fichier verrouille : sans consequence */ }
                }

                if (todo.Count == 0)
                {
                    State.busy = false;
                    State.canPlay = true;
                    State.progress = 1;
                    // La version est deja affichee en pied de page.
                    Set("ready", "The client is up to date.", "");
                    return;
                }

                // 6. Le dossier du jeu est-il accessible en ecriture ? Sous Program Files
                //    il faut repasser par UAC — mais seulement si une mise a jour est due.
                if (!IsWritable(Config.GamePath))
                {
                    State.busy = false;
                    Set("error", "Droits administrateur requis.",
                        "The game folder is protected by Windows.");
                    Action h = ElevationNeeded;
                    if (h != null) h();
                    return;
                }

                // 7. Telecharger.
                State.filesTodo = todo.Count;
                State.bytesTotal = totalBytes;
                State.bytesDone = 0;
                State.progress = 0;
                Set("downloading", todo.Count + " file(s) to update", "");

                long done = 0;
                foreach (FileEntry f in todo)
                {
                    if (_stop) return;
                    done = DownloadOne(f, done, totalBytes);
                }

                State.busy = false;
                State.canPlay = true;
                State.progress = 1;
                Set("ready", "Update complete.", "");
            }
            catch (Exception ex)
            {
                State.busy = false;
                State.canPlay = true;   // on n'empeche pas de jouer
                Set("error", "Update failed.", ex.Message);
            }
        }

        private static string ShortName(string relPath)
        {
            int i = relPath.LastIndexOf('/');
            return i < 0 ? relPath : relPath.Substring(i + 1);
        }

        // -- Manifeste ---------------------------------------------------------

        private static Manifest FetchManifest(string url)
        {
            string json;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "SGWLauncher/" + Program.LauncherVersion;
            req.Timeout = 20000;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = reader.ReadToEnd();

            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Manifeste illisible.");

            var m = new Manifest();
            m.Version = Str(root, "version", "");

            // baseUrl peut etre relative au manifeste.
            string baseUrl = Str(root, "baseUrl", "");
            Uri baseUri = string.IsNullOrEmpty(baseUrl) ? new Uri(url) : new Uri(new Uri(url), baseUrl);

            object filesObj;
            if (root.TryGetValue("files", out filesObj))
            {
                var files = filesObj as object[];
                if (files != null)
                    foreach (object o in files)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        string rel = Str(d, "path", null);
                        string sha = Str(d, "sha256", null);
                        if (rel == null || sha == null || !IsSafeRelativePath(rel)) continue;

                        var fe = new FileEntry();
                        fe.Path = rel;
                        fe.Sha256 = sha.ToLowerInvariant();
                        fe.Size = Num(d, "size");
                        fe.Channel = Str(d, "channel", "player");
                        fe.Url = new Uri(baseUri, EscapeRelative(rel)).ToString();
                        m.Files.Add(fe);
                    }
            }

            object removeObj;
            if (root.TryGetValue("remove", out removeObj))
            {
                var remove = removeObj as object[];
                if (remove != null)
                    foreach (object o in remove)
                    {
                        string s = o as string;
                        if (s != null) m.Remove.Add(s);
                    }
            }

            object shardsObj;
            if (root.TryGetValue("shards", out shardsObj))
            {
                var shards = shardsObj as object[];
                if (shards != null)
                    foreach (object o in shards)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        string arg = Str(d, "arg", null);
                        if (arg == null) continue;
                        m.Shards.Add(new Shard { arg = arg, name = Str(d, "name", arg) });
                    }
            }

            // Fond optionnel : permet de changer l'illustration a chaque publication
            // sans recompiler ni redistribuer le launcher.
            string splash = Str(root, "splash", null);
            if (!string.IsNullOrEmpty(splash))
            {
                try { m.SplashUrl = new Uri(baseUri, EscapeRelative(splash)).ToString(); }
                catch { /* URL invalide : on garde l'image embarquee */ }
            }

            object launcherObj;
            if (root.TryGetValue("launcher", out launcherObj))
            {
                var launcher = launcherObj as Dictionary<string, object>;
                if (launcher != null)
                {
                    m.LauncherVersion = Str(launcher, "version", null);
                    m.LauncherSha256 = Str(launcher, "sha256", null);
                    string lp = Str(launcher, "path", null);
                    if (lp != null) m.LauncherUrl = new Uri(baseUri, EscapeRelative(lp)).ToString();
                }
            }

            return m;
        }

        private static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }

        private static long Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
                catch { }
            }
            return 0;
        }

        private static string EscapeRelative(string rel)
        {
            string[] parts = rel.Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        // Le manifeste vient du reseau : on refuse tout chemin qui sortirait du jeu.
        private static bool IsSafeRelativePath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            if (rel.IndexOf(':') >= 0) return false;
            if (rel.StartsWith("/") || rel.StartsWith("\\")) return false;
            foreach (string part in rel.Replace('\\', '/').Split('/'))
                if (part == "..") return false;
            return rel.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }

        // -- Droits d'ecriture --------------------------------------------------

        public static bool IsWritable(string gamePath)
        {
            try
            {
                string probe = Path.Combine(gamePath, "Working", ".sgw-write-test");
                using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write,
                                               FileShare.None, 1, FileOptions.DeleteOnClose))
                    fs.WriteByte(0);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
            catch { return true; }   // autre cause : le telechargement tranchera
        }

        public static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // -- Hash et telechargement ---------------------------------------------

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool HashEquals(string path, string expected)
        {
            try { return string.Equals(Sha256File(path), expected, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // Telecharge un fichier, verifie son hash, puis le met en place.
        private long DownloadOne(FileEntry f, long doneBytes, long totalBytes)
        {
            string dest = Path.Combine(Config.GamePath, f.Path.Replace('/', '\\'));
            string tmp = dest + ".sgwtmp";
            Directory.CreateDirectory(Path.GetDirectoryName(dest));

            string shortName = ShortName(f.Path);
            State.detail = shortName;
            Emit();

            var req = (HttpWebRequest)WebRequest.Create(f.Url);
            req.UserAgent = "SGWLauncher/" + Program.LauncherVersion;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;

            long fileDone = 0;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 16];
                int read;
                long lastReport = 0;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (_stop) throw new OperationCanceledException();
                    dst.Write(buffer, 0, read);
                    fileDone += read;

                    // Rafraichir l'interface ~tous les 256 ko plutot qu'a chaque bloc.
                    if (fileDone - lastReport >= (1 << 18))
                    {
                        lastReport = fileDone;
                        State.bytesDone = doneBytes + fileDone;
                        State.progress = totalBytes > 0 ? (double)State.bytesDone / totalBytes : 0;
                        Emit();
                    }
                }
            }

            if (!string.Equals(Sha256File(tmp), f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tmp); } catch { }
                throw new InvalidDataException("File corrupted during download: " + f.Path);
            }

            // File.Replace echoue entre volumes ou si la cible est absente : on fait
            // la permutation a la main.
            try
            {
                if (File.Exists(dest))
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                    File.Delete(dest);
                }
                File.Move(tmp, dest);
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException("Could not replace " + f.Path +
                                      " (is the game currently running?)", ex);
            }

            State.bytesDone = doneBytes + fileDone;
            State.progress = totalBytes > 0 ? (double)State.bytesDone / totalBytes : 1;
            Emit();
            return State.bytesDone;
        }

        // -- Auto-mise a jour du launcher ---------------------------------------

        // Retourne true si le relais a demarre : l'hote doit fermer l'application.
        private bool SelfUpdate(string url, string expectedSha)
        {
            try
            {
                string exe = Program.ExePath;
                string newExe = exe + ".new";

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "SGWLauncher/" + Program.LauncherVersion;
                req.Timeout = 30000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream src = resp.GetResponseStream())
                using (var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write))
                    src.CopyTo(dst);

                if (!string.IsNullOrEmpty(expectedSha) && !HashEquals(newExe, expectedSha))
                {
                    File.Delete(newExe);
                    return false;
                }

                // Un .cmd fait la permutation une fois ce processus termine.
                //
                // Ce script est ecrit dans le dossier du launcher (%LOCALAPPDATA%)
                // et non dans %TEMP%, il reste visible pendant son execution et ne
                // s'auto-supprime pas. La version precedente cumulait les trois
                // traits inverses -- script cache dans %TEMP%, fenetre masquee,
                // « del "%~f0" » final, « ping » en guise de pause -- c'est-a-dire
                // la signature exacte d'un dropper. Les moteurs par apprentissage
                // (Defender Wacatac.B!ml, Malwarebytes MachineLearning/Anomalous)
                // la reconnaissent, d'ou la mise en quarantaine du launcher.
                //
                // Le comportement fonctionnel est inchange : attendre la fermeture
                // du processus, permuter les fichiers, relancer.
                //
                // La pause fixe est remplacee par un reessai du deplacement jusqu'a
                // ce qu'il aboutisse. C'est plus sur qu'un delai en dur, qui echoue
                // en silence sur une machine lente a fermer le processus, et ca
                // supprime le besoin d'un idiome de temporisation.
                //
                // « waitfor » et non « timeout » pour la seconde d'attente : timeout
                // refuse de s'executer des que l'entree standard est redirigee
                // (« la redirection de l'entree n'est pas prise en charge ») et rend
                // la main aussitot. C'est justement ce defaut qui pousse a ecrire
                // « ping -n 3 127.0.0.1 », et donc a ressembler a un dropper.
                string cmd = Path.Combine(LauncherConfig.Dir, "maj-launcher.cmd");
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("title The Fifth Race launcher update");
                sb.AppendLine("echo Updating the launcher, please wait...");
                sb.AppendLine("set essais=0");
                sb.AppendLine(":reessai");
                sb.AppendLine("move /y \"" + exe + "\" \"" + exe + ".old\" >nul 2>&1");
                sb.AppendLine("if not errorlevel 1 goto permuter");
                sb.AppendLine("set /a essais+=1");
                sb.AppendLine("if %essais% geq 30 goto echec");
                sb.AppendLine("waitfor /t 1 SgwMajLauncher >nul 2>&1");
                sb.AppendLine("goto reessai");
                sb.AppendLine(":permuter");
                sb.AppendLine("move /y \"" + newExe + "\" \"" + exe + "\" >nul");
                sb.AppendLine("start \"\" \"" + exe + "\"");
                sb.AppendLine("goto :eof");
                sb.AppendLine(":echec");
                // Le premier move n'a jamais abouti : l'executable d'origine est
                // intact, on relance la version actuelle plutot que de laisser le
                // joueur devant rien.
                sb.AppendLine("echo The update could not be applied. Restarting the current version.");
                sb.AppendLine("del \"" + newExe + "\" >nul 2>&1");
                sb.AppendLine("start \"\" \"" + exe + "\"");
                File.WriteAllText(cmd, sb.ToString(), Encoding.Default);

                // Fenetre reduite et non masquee : le joueur voit ce qui se passe,
                // et le lancement d'un script invisible cesse d'etre un signal.
                var psi = new ProcessStartInfo(cmd);
                psi.WindowStyle = ProcessWindowStyle.Minimized;
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;   // on poursuit avec le launcher actuel
            }
        }

        // -- Lancement du jeu ----------------------------------------------------

        public string Play()
        {
            string arg = string.IsNullOrEmpty(State.shard) ? "PRODLIVE" : State.shard;
            string working = Path.Combine(Config.GamePath, "Working");
            string binaries = Path.Combine(working, "binaries");
            string exe = Path.Combine(binaries, "SGW.exe");

            if (!File.Exists(exe)) return "SGW.exe was not found in:\n" + binaries;

            // Equivalent du « attrib -r » des .bat d'origine : le jeu doit pouvoir
            // reecrire son cache de shaders.
            try
            {
                string shaderCache = Path.Combine(working, @"SGWGame\Content\LocalShaderCache.upk");
                if (File.Exists(shaderCache))
                {
                    FileAttributes a = File.GetAttributes(shaderCache);
                    if ((a & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(shaderCache, a & ~FileAttributes.ReadOnly);
                }
            }
            catch { /* pas bloquant */ }

            try
            {
                var psi = new ProcessStartInfo(exe, "-s " + arg);
                psi.WorkingDirectory = binaries;
                psi.UseShellExecute = true;
                Process.Start(psi);
                return null;
            }
            catch (Exception ex)
            {
                return "The game could not start:\n\n" + ex.Message;
            }
        }
    }
}
