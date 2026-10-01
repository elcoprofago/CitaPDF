using System.IO;
using System.Text;
using System.Text.Json;

namespace CitaPDF.Servicios
{
    // Carga/guardado de biblioteca.json -- el único archivo de datos de la
    // app (sin carpeta de PDFs, ver plan). Reescribir la lista completa en
    // cada alta es instantáneo a la escala de una biblioteca personal, mismo
    // criterio que config.json en los proyectos hermanos.
    public static class Biblioteca
    {
        private static readonly JsonSerializerOptions JsonOptsLectura =
            new() { PropertyNameCaseInsensitive = true };

        private static readonly JsonSerializerOptions JsonOptsEscritura =
            new() { WriteIndented = true };

        // Modo portable: si junto al .exe existe la carpeta "datos" (la crea
        // el perfil de publicación), los datos viven ahí y viajan con el
        // pendrive. Si no existe (p. ej. compilación Debug), se usa
        // Documentos\CitaPDF como siempre.
        private static string? _dataDir;

        // Resultado de la copia inicial a "datos", para que MainWindow lo
        // muestre en el log al arrancar (null si no hubo nada que informar).
        public static string? MensajeArranque { get; private set; }
        public static bool MensajeArranqueEsError { get; private set; }

        private static string DocumentosDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CitaPDF");

        private static string PortableDir => Path.Combine(Rutas.BaseDir, "datos");

        public static string GetDataDir()
        {
            if (_dataDir != null) return _dataDir;

            string dir = DocumentosDir;
            if (Directory.Exists(PortableDir))
            {
                dir = PortableDir;
                if (!CopiarDesdeDocumentosSiCorresponde())
                    dir = DocumentosDir;
            }
            Directory.CreateDirectory(dir);
            _dataDir = dir;
            return dir;
        }

        // Primera vez de la versión portable en una PC que ya tenía datos en
        // Documentos: se COPIAN (nunca se mueven ni se pisan) a "datos". Sólo
        // si "datos" no tiene todavía ni biblioteca.json ni config.json: si
        // ya tiene algo, esos son los datos portables y no se tocan.
        // Devuelve false si la copia falló -- en ese caso esta sesión sigue
        // en Documentos, para no arrancar con una biblioteca vacía que
        // parezca la real.
        private static bool CopiarDesdeDocumentosSiCorresponde()
        {
            string[] nombres = { "biblioteca.json", "config.json" };
            if (nombres.Any(n => File.Exists(Path.Combine(PortableDir, n)))) return true;
            if (!nombres.Any(n => File.Exists(Path.Combine(DocumentosDir, n)))) return true;

            // Lo creado en este intento: si algo falla se borra, para que la
            // próxima ejecución no tome una copia a medias como datos
            // portables válidos (sólo esto -- "datos" estaba vacío de .json).
            var creados = new List<string>();
            try
            {
                foreach (var n in nombres)
                {
                    string origen = Path.Combine(DocumentosDir, n);
                    if (!File.Exists(origen)) continue;
                    string destino = Path.Combine(PortableDir, n);
                    byte[] contenido = File.ReadAllBytes(origen);
                    creados.Add(destino + ".tmp");
                    File.WriteAllBytes(destino + ".tmp", contenido);
                    File.Move(destino + ".tmp", destino, overwrite: false);
                    creados.Add(destino);
                    if (!contenido.AsSpan().SequenceEqual(File.ReadAllBytes(destino)))
                        throw new IOException($"La copia de {n} no coincide con el original.");
                }
                MensajeArranque = $"Primera ejecución portable: se copiaron los datos de {DocumentosDir} a {PortableDir} (los originales quedan intactos).";
                return true;
            }
            catch (Exception ex)
            {
                foreach (var f in creados)
                {
                    try { File.Delete(f); } catch { }
                }
                MensajeArranque = $"No se pudieron copiar los datos a {PortableDir} ({ex.Message}). Esta sesión usa {DocumentosDir}.";
                MensajeArranqueEsError = true;
                return false;
            }
        }

        public static string GetConfigPath() => Path.Combine(GetDataDir(), "config.json");

        public static string GetBibliotecaPath() => Path.Combine(GetDataDir(), "biblioteca.json");

        public static AppSettings CargarSettings()
        {
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path)) return new AppSettings();
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptsLectura) ?? new AppSettings();
            }
            catch
            {
                // Config corrupta o ilegible: seguir con los valores por
                // defecto en vez de impedir que la app arranque.
                return new AppSettings();
            }
        }

        public static void GuardarSettings(AppSettings settings)
        {
            string json = JsonSerializer.Serialize(settings, JsonOptsEscritura);
            EscribirAtomico(GetConfigPath(), json);
        }

        public static List<DocumentoRecord> CargarDocumentos()
        {
            try
            {
                string path = GetBibliotecaPath();
                if (!File.Exists(path)) return new List<DocumentoRecord>();
                string json = File.ReadAllText(path, Encoding.UTF8);
                var archivo = JsonSerializer.Deserialize<BibliotecaFile>(json, JsonOptsLectura);
                return archivo?.Documentos ?? new List<DocumentoRecord>();
            }
            catch
            {
                // biblioteca.json corrupto: no perder el arranque de la app
                // por un archivo dañado -- se ve como biblioteca vacía, no
                // como crash.
                return new List<DocumentoRecord>();
            }
        }

        public static void GuardarDocumentos(List<DocumentoRecord> documentos)
        {
            var archivo = new BibliotecaFile { Documentos = documentos };
            string json = JsonSerializer.Serialize(archivo, JsonOptsEscritura);
            EscribirAtomico(GetBibliotecaPath(), json);
        }

        // Copia de respaldo de biblioteca.json a una ruta elegida por el
        // usuario. Se copia vía .tmp + Move (mismo criterio que
        // EscribirAtomico) para que un respaldo anterior en el destino nunca
        // quede a medio pisar, y después se relee la copia: devuelve la
        // cantidad de documentos verificados, o lanza si la copia no coincide.
        public static int ExportarCopia(string destino)
        {
            string origen = GetBibliotecaPath();
            if (!File.Exists(origen))
                throw new FileNotFoundException("Todavía no hay biblioteca.json para copiar.", origen);

            if (string.Equals(Path.GetFullPath(destino), Path.GetFullPath(origen), StringComparison.OrdinalIgnoreCase))
                throw new IOException("El destino es el mismo biblioteca.json en uso. Elegí otra ubicación o nombre.");

            byte[] contenido = File.ReadAllBytes(origen);
            string tmp = destino + ".tmp";
            File.WriteAllBytes(tmp, contenido);
            File.Move(tmp, destino, overwrite: true);

            byte[] copia = File.ReadAllBytes(destino);
            if (!contenido.AsSpan().SequenceEqual(copia))
                throw new IOException("La copia escrita no coincide con el original.");

            // ReadAllText y no Encoding.UTF8.GetString: biblioteca.json lleva
            // BOM (EscribirAtomico) y GetString lo deja pegado al JSON.
            var archivo = JsonSerializer.Deserialize<BibliotecaFile>(File.ReadAllText(destino, Encoding.UTF8), JsonOptsLectura);
            return archivo?.Documentos.Count ?? 0;
        }

        // Escritura vía archivo temporal + File.Move: si la app se cierra a
        // mitad de una escritura, biblioteca.json nunca queda truncado --
        // en el peor caso queda un .tmp huérfano, nunca datos corruptos.
        private static void EscribirAtomico(string path, string contenido)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contenido, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }

        public static string GenerarNuevoId(List<DocumentoRecord> existentes)
        {
            int max = 0;
            foreach (var doc in existentes)
            {
                if (doc.DocumentoId.StartsWith("CT-") &&
                    int.TryParse(doc.DocumentoId.AsSpan(3), out int n) && n > max)
                {
                    max = n;
                }
            }
            return $"CT-{max + 1:D4}";
        }
    }
}
