using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;

namespace CitaPDF.Servicios
{
    // Ciclo de vida del llama-server propio de CitaPDF -- mismo binario y
    // modelo que usan CSJN/PGN/Académico, pero en su propio puerto (9002)
    // para poder correr standalone. Arranque perezoso (primer PDF agregado
    // en la sesión), sin botón "Iniciar LLM": acá el LLM es un detalle de
    // implementación interno, no algo que el usuario deba operar.
    public static class LlamaServerProceso
    {
        public const int Puerto = 9002;

        private const string ModeloPath =
            @"E:\Models\bartowski\Phi-3-medium-128k-instruct-GGUF\Phi-3-medium-128k-instruct-Q3_K_S.gguf";
        private const string ServidorExe =
            @"C:\Users\Rodolfo\.lmstudio\extensions\backends\llama.cpp-win-x86_64-nvidia-cuda12-avx2-2.23.1\llama-server.exe";
        private const string VendorPath =
            @"C:\Users\Rodolfo\.lmstudio\extensions\backends\vendor\win-llama-cuda12-vendor-v2";

        private static Process? _proceso;

        // Últimas líneas de stderr del proceso -- si llama-server se cierra
        // inesperadamente durante el arranque (crash, falta de VRAM, etc.),
        // esto es lo único que permite diagnosticar por qué sin adivinar.
        private static readonly Queue<string> _ultimasLineasError = new();
        private const int MaxLineasError = 20;

        public static bool EstaCorriendo => _proceso != null && !_proceso.HasExited;

        // log recibe (mensaje, nivel, overwrite) -- mismos parámetros que
        // MainWindow.Log(msg, level, overwrite).
        public static async Task<bool> AsegurarIniciadoAsync(Action<string, string, bool>? log = null)
        {
            if (EstaCorriendo) return true;

            if (!File.Exists(ServidorExe) || !File.Exists(ModeloPath))
            {
                log?.Invoke("No se encontró llama-server.exe o el archivo del modelo. Revisá las rutas configuradas.", "ERROR", false);
                return false;
            }

            log?.Invoke($"Iniciando modelo local (puerto {Puerto})... puede tardar unos segundos.", "SPINNER", false);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ServidorExe,
                    WorkingDirectory = Path.GetDirectoryName(ServidorExe),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(ModeloPath);
                psi.ArgumentList.Add("--host"); psi.ArgumentList.Add("127.0.0.1");
                psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(Puerto.ToString());
                psi.ArgumentList.Add("--ctx-size"); psi.ArgumentList.Add("8192");
                psi.ArgumentList.Add("--n-gpu-layers"); psi.ArgumentList.Add("99");
                psi.ArgumentList.Add("--parallel"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("--flash-attn"); psi.ArgumentList.Add("on");
                psi.ArgumentList.Add("--cache-type-k"); psi.ArgumentList.Add("q8_0");
                psi.ArgumentList.Add("--cache-type-v"); psi.ArgumentList.Add("q8_0");

                psi.EnvironmentVariables["PATH"] = VendorPath + ";" + Environment.GetEnvironmentVariable("PATH");

                _ultimasLineasError.Clear();
                _proceso = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proceso.OutputDataReceived += (s, ev) => { };
                _proceso.ErrorDataReceived += (s, ev) =>
                {
                    if (string.IsNullOrEmpty(ev.Data)) return;
                    lock (_ultimasLineasError)
                    {
                        _ultimasLineasError.Enqueue(ev.Data);
                        if (_ultimasLineasError.Count > MaxLineasError) _ultimasLineasError.Dequeue();
                    }
                };
                _proceso.Start();
                _proceso.BeginOutputReadLine();
                _proceso.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                log?.Invoke($"No se pudo iniciar llama-server: {ex.Message}", "ERROR", false);
                return false;
            }

            return await EsperarSaludAsync(log);
        }

        private static async Task<bool> EsperarSaludAsync(Action<string, string, bool>? log)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var limite = DateTime.UtcNow.AddSeconds(120);

            while (DateTime.UtcNow < limite)
            {
                if (_proceso == null || _proceso.HasExited)
                {
                    string detalle;
                    lock (_ultimasLineasError) detalle = string.Join(" | ", _ultimasLineasError);
                    log?.Invoke(string.IsNullOrWhiteSpace(detalle)
                        ? "llama-server se cerró inesperadamente durante el arranque."
                        : $"llama-server se cerró inesperadamente durante el arranque: {detalle}", "ERROR", false);
                    return false;
                }

                try
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{Puerto}/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        log?.Invoke("Modelo local listo.", "OK", true);
                        return true;
                    }
                }
                catch
                {
                    // Todavía no levantó el socket -- reintentar.
                }

                await Task.Delay(1000);
            }

            log?.Invoke("Tiempo de espera agotado iniciando el modelo local.", "ERROR", false);
            return false;
        }

        public static void Detener()
        {
            if (_proceso == null) return;
            try
            {
                if (!_proceso.HasExited)
                    _proceso.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ya se estaba cerrando o el handle dejó de ser válido: no
                // hay nada más que hacer.
            }
            _proceso = null;
        }

        // Purga instancias de sesiones anteriores que hayan quedado
        // corriendo (crash, cierre forzado desde el IDE, etc.). El
        // fingerprint es exe idéntico + "--port 9002" en la línea de
        // comando -- el mismo binario lo usan CSJN/PGN (9000) y Académico
        // (9001), así que el puerto es lo único que distingue "el propio"
        // de una instancia hermana viva.
        public static void PurgarHuerfanos()
        {
            string puertoTag = $"--port {Puerto}";
            var propios = new HashSet<int>();
            if (_proceso != null && !_proceso.HasExited) propios.Add(_proceso.Id);

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'llama-server.exe'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    int pid = Convert.ToInt32(mo["ProcessId"]);
                    if (propios.Contains(pid)) continue;

                    string exe = mo["ExecutablePath"]?.ToString() ?? "";
                    string cmd = mo["CommandLine"]?.ToString() ?? "";

                    bool esNuestro = exe.Equals(ServidorExe, StringComparison.OrdinalIgnoreCase)
                        && cmd.Contains(puertoTag, StringComparison.OrdinalIgnoreCase);
                    if (!esNuestro) continue;

                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/PID {pid} /T /F",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(5000);
                    }
                    catch
                    {
                        // Un huérfano que no se pudo matar no debería
                        // impedir que el resto del arranque siga.
                    }
                }
            }
            catch (ManagementException)
            {
                // WMI no disponible: no bloquea el arranque de la app.
            }
        }
    }
}
