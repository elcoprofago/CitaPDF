using System.IO;
using System.Security.Cryptography;

namespace CitaPDF.Servicios
{
    // PDFs catalogados que cambiaron de lugar. El documento se reconoce por
    // su HashSha256 (el contenido del PDF), no por el nombre: uno movido a
    // otra carpeta o renombrado se encuentra igual.
    public static class Ubicador
    {
        public static bool FaltaArchivo(DocumentoRecord d) =>
            !string.IsNullOrWhiteSpace(d.RutaArchivoOriginal) && !File.Exists(d.RutaArchivoOriginal);

        // Mismo formato que PdfValidacion.CalcularHashSha256, pero leyendo
        // por partes en vez de cargar el PDF entero en memoria.
        public static string CalcularHash(string ruta)
        {
            using var fs = File.OpenRead(ruta);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        // Carpeta existente más cercana a la ruta vieja, para abrir los
        // diálogos de elegir archivo/carpeta ahí. null si no queda ninguna.
        public static string? CarpetaExistenteMasCercana(string? ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta)) return null;
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(ruta));
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch (Exception) { return null; }
        }

        // Recorre 'carpeta' y sus subcarpetas buscando PDFs con alguno de los
        // hashes pedidos (hash -> nombre de archivo anterior). Devuelve
        // hash -> ruta encontrada. Revisa primero los que se siguen llamando
        // igual (lo más común: sólo se movieron) y termina apenas encontró
        // todos.
        public static Dictionary<string, string> Buscar(string carpeta, IReadOnlyDictionary<string, string> buscados,
            IProgress<string>? progreso, CancellationToken ct)
        {
            var opciones = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                // Sin entrar en junctions ni enlaces simbólicos: pueden formar
                // ciclos y recorrer el mismo árbol una y otra vez. Los ocultos
                // sí se revisan (el valor por defecto los saltea).
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };

            var archivos = new List<string>();
            foreach (var a in Directory.EnumerateFiles(carpeta, "*.pdf", opciones))
            {
                ct.ThrowIfCancellationRequested();
                archivos.Add(a);
                if (archivos.Count % 200 == 0) progreso?.Report($"Listando PDFs... {archivos.Count}");
            }

            var nombres = new HashSet<string>(buscados.Values.Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
            var ordenados = archivos.OrderBy(a => nombres.Contains(Path.GetFileName(a)) ? 0 : 1).ToList();

            var encontrados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordenados.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string a = ordenados[i];
                progreso?.Report($"Revisando {i + 1} de {ordenados.Count}: {Path.GetFileName(a)}");
                string h;
                try { h = CalcularHash(a); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (buscados.ContainsKey(h) && encontrados.TryAdd(h, a) && encontrados.Count == buscados.Count) break;
            }
            return encontrados;
        }
    }
}
