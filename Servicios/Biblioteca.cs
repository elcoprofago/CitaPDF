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

        public static string GetDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CitaPDF");
            Directory.CreateDirectory(dir);
            return dir;
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
