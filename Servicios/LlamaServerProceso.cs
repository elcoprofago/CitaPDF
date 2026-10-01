using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;

namespace CitaPDF.Servicios
{
    // Ciclo de vida del llama-server propio de CitaPDF -- mismo modelo que
    // usan CSJN/PGN/Académico, pero en su propio puerto (9002)
    // para poder correr standalone. Arranque perezoso (primer PDF agregado
    // en la sesión), sin botón "Iniciar LLM": acá el LLM es un detalle de
    // implementación interno, no algo que el usuario deba operar.
    public static class LlamaServerProceso
    {
        public const int Puerto = 9002;

        // Rutas efectivas, fijadas desde config.json con Configurar(). El
        // build de llama.cpp tiene que ser autónomo (sus DLL de CUDA en la
        // misma carpeta): no se suma nada al PATH.
        public static string ServidorExe { get; private set; } = "";
        public static string ModeloPath { get; private set; } = "";

        // Detección automática cuando config.json no tiene ruta elegida:
        // primero la estructura portable junto al .exe, después las rutas
        // de esta PC que se usaban antes de que existiera la configuración.
        private static readonly string ServidorAnterior = @"E:\llama-server\llama-server.exe";
        private static readonly string ModeloAnterior =
            @"E:\Models\bartowski\Phi-3-medium-128k-instruct-GGUF\Phi-3-medium-128k-instruct-Q3_K_S.gguf";

        public static void Configurar(AppSettings settings)
        {
            ServidorExe = Rutas.Resolver(settings.RutaServidor);
            if (ServidorExe == "")
            {
                string portable = Path.Combine(Rutas.BaseDir, "llama-server", "llama-server.exe");
                ServidorExe = File.Exists(portable) ? portable : ServidorAnterior;
            }

            ModeloPath = Rutas.Resolver(settings.RutaModelo);
            if (ModeloPath == "")
            {
                string carpeta = Path.Combine(Rutas.BaseDir, "modelos");
                string? portable = Directory.Exists(carpeta)
                    ? Directory.EnumerateFiles(carpeta, "*.gguf", SearchOption.AllDirectories)
                        .Where(BuscadorArchivos.EsModeloPrincipal)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault()
                    : null;
                ModeloPath = portable ?? ModeloAnterior;
            }
        }

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

            if (!File.Exists(ServidorExe))
            {
                log?.Invoke($"No se encontró llama-server.exe en {ServidorExe}", "ERROR", false);
                return false;
            }
            if (!File.Exists(ModeloPath))
            {
                log?.Invoke($"No se encontró el modelo en {ModeloPath}", "ERROR", false);
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
                // 8192 alcanzaba con el tope viejo de texto (6000 caracteres);
                // al subir la extracción a 10 páginas / 20000 caracteres
                // (ExtraccionLlm.MaxCaracteresTexto) algunos documentos con
                // texto denso superaban el contexto y llama-server devolvía
                // 400 Bad Request. 32768 lo resolvía, pero llama.cpp reserva
                // el caché KV para TODO --ctx-size ya al arrancar (no según
                // el prompt real): cuadruplicar el contexto cuadruplica esa
                // reserva de VRAM, y si eso no entra junto con los pesos del
                // modelo, --n-gpu-layers 99 igual intenta cargar todo y el
                // resto termina compitiendo por memoria -- generación mucho
                // más lenta incluso en documentos cortos. 16384 alcanza de
                // sobra para 20000 caracteres (~10000 tokens en el peor caso)
                // con una reserva de KV cache bastante menor.
                psi.ArgumentList.Add("--ctx-size"); psi.ArgumentList.Add("16384");
                psi.ArgumentList.Add("--n-gpu-layers"); psi.ArgumentList.Add("99");
                psi.ArgumentList.Add("--parallel"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("--flash-attn"); psi.ArgumentList.Add("on");
                psi.ArgumentList.Add("--cache-type-k"); psi.ArgumentList.Add("q8_0");
                psi.ArgumentList.Add("--cache-type-v"); psi.ArgumentList.Add("q8_0");

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

        // Spinner estilo consola (mismo set de caracteres que PostOCRNormalizer)
        // -- acá se anima con el propio polling de /health cada 1s en vez de
        // un DispatcherTimer aparte, ya que el ciclo ya existe.
        private static readonly string[] SpinnerFrames = { "/", "-", "\\", "|" };

        private static async Task<bool> EsperarSaludAsync(Action<string, string, bool>? log)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var limite = DateTime.UtcNow.AddSeconds(120);
            int frame = 0;

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

                log?.Invoke($"Iniciando modelo local (puerto {Puerto})... {SpinnerFrames[frame++ % SpinnerFrames.Length]}", "SPINNER", true);

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
