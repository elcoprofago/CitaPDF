using System.IO;
using System.Text.RegularExpressions;

namespace CitaPDF.Servicios
{
    // Búsqueda de llama-server.exe / modelos .gguf en todas las unidades,
    // para elegir desde SettingsWindow. Recorrido manual (no
    // SearchOption.AllDirectories) para poder:
    //   - NO seguir junctions ni symlinks (ReparsePoint): seguirlos produce
    //     ciclos y resultados duplicados -- mismo problema que robocopy sin /XJ;
    //   - saltear carpetas inaccesibles sin abortar todo el recorrido;
    //   - saltear carpetas del sistema enormes donde nunca hay modelos;
    //   - cancelar y reportar progreso.
    public static class BuscadorArchivos
    {
        private static readonly HashSet<string> CarpetasExcluidas = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "$Recycle.Bin", "System Volume Information", "$WinREAgent",
            "node_modules", ".git", "WinSxS",
        };

        // Proyectores de visión (mmproj-*.gguf), vocabularios sueltos de las
        // pruebas de llama.cpp (ggml-vocab-*.gguf, sin pesos) y partes 2..N
        // de un modelo dividido no se pueden cargar solos con --model: no se
        // ofrecen.
        private static readonly Regex ParteDividida = new(@"-(\d+)-of-\d+\.gguf$", RegexOptions.IgnoreCase);

        public static bool EsModeloPrincipal(string ruta)
        {
            string nombre = Path.GetFileName(ruta);
            if (nombre.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
                || nombre.StartsWith("ggml-vocab-", StringComparison.OrdinalIgnoreCase)) return false;
            var m = ParteDividida.Match(nombre);
            return !m.Success || int.Parse(m.Groups[1].Value) == 1;
        }

        public static List<string> Buscar(string patron, Func<string, bool>? filtro,
            IProgress<string>? progreso, CancellationToken ct, IEnumerable<string>? raices = null)
        {
            var resultados = new List<string>();
            // Avisar cada carpeta saturaría el hilo de la UI con miles de
            // actualizaciones por segundo: alcanza con una cada 100 ms.
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            var unidades = raices ?? DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .Select(d => d.RootDirectory.FullName);

            foreach (var raiz in unidades)
            {
                var pendientes = new Stack<string>();
                pendientes.Push(raiz);
                while (pendientes.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    string dir = pendientes.Pop();
                    if (reloj.ElapsedMilliseconds >= 100)
                    {
                        progreso?.Report(dir);
                        reloj.Restart();
                    }

                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, patron))
                            if (filtro == null || filtro(f)) resultados.Add(f);

                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            var info = new DirectoryInfo(sub);
                            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (CarpetasExcluidas.Contains(info.Name)) continue;
                            pendientes.Push(sub);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // Carpeta sin permiso o que desapareció: seguir con el resto.
                    }
                }
            }
            return resultados;
        }
    }
}
