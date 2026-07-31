using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CitaPDF.Servicios
{
    public class ExtraccionResultado
    {
        public string Titulo { get; set; } = "";
        public List<string> AutoresApa { get; set; } = new();
        public string Anio { get; set; } = "";
        public string Editorial { get; set; } = "";
        public bool Exito { get; set; }
    }

    // Llama al llama-server propio (puerto 9002) pidiendo JSON estructurado
    // con los datos bibliográficos de las primeras páginas -- el LLM sólo
    // extrae datos (autores ya formateados en estilo APA), nunca redacta la
    // cita final: eso lo hace CitaApa.Construir de forma determinística.
    public static class ExtraccionLlm
    {
        private const string Modelo = "Phi-3-medium-128k-instruct-Q3_K_S.gguf";

        private const string PromptBase = """
            Sos un asistente que extrae datos bibliográficos de las primeras páginas de
            un documento (libro, artículo, capítulo, fallo, etc.) para armar una cita en
            formato APA (7ª edición).

            Extraé, a partir del texto que te paso:
            - "titulo": título completo de la obra.
            - "autores_apa": lista de autores, cada uno YA formateado en estilo APA
              ("Apellido, N. N."), en el orden en que figuran. Si el apellido es
              compuesto, conservalo completo (ej: "Fernández Blanco, C.").
            - "anio": año de publicación (4 dígitos), o "s.f." si no figura.
            - "editorial": editorial, revista o fuente de publicación (cadena vacía si no
              figura).

            Si un dato no está en el texto, dejalo vacío ("" o [] según corresponda) --
            NUNCA inventes datos que no figuran en el texto.

            Respondé ÚNICAMENTE con un objeto JSON válido, sin texto adicional, con este
            formato exacto:
            {"titulo": "...", "autores_apa": ["..."], "anio": "...", "editorial": "..."}

            Texto:
            """;

        // Tope de caracteres del texto que se manda al prompt -- acotado para
        // no disparar un prompt gigante y una latencia desproporcionada en un
        // modelo local, pero lo bastante amplio como para cubrir las ~10
        // páginas que ahora extrae PdfTexto (algunos documentos tienen los
        // datos editoriales recién en una portadilla varias páginas adentro).
        private const int MaxCaracteresTexto = 20000;

        public static async Task<ExtraccionResultado> ExtraerAsync(string textoPrimerasPaginas)
        {
            string texto = textoPrimerasPaginas.Length > MaxCaracteresTexto
                ? textoPrimerasPaginas[..MaxCaracteresTexto]
                : textoPrimerasPaginas;
            string prompt = PromptBase + "\n" + texto;

            var payload = new
            {
                model = Modelo,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.0,
                max_tokens = 500,
                response_format = new { type = "json_object" },
                stream = false,
            };

            // Generar en un modelo local puede tardar bastante (medido:
            // ~45s para un documento chico) -- un timeout generoso evita
            // descartar una extracción que sólo iba lenta. Con el tope de
            // texto en 20000 caracteres, un artículo breve pero con prosa
            // densa (poco margen/whitespace, típico de un paper corto) puede
            // generar un prompt tan largo como el de un libro de más
            // páginas, así que el margen tiene que cubrir ese caso también.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
            using var resp = await http.PostAsJsonAsync(
                $"http://127.0.0.1:{LlamaServerProceso.Puerto}/v1/chat/completions", payload);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string contenido = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return ParsearRespuesta(contenido);
        }

        // El modelo a veces envuelve el JSON en un bloque de código
        // (```json ... ``` o '''json ... ''') pese a que response_format
        // pide json_object puro (confirmado empíricamente con
        // Phi-3-medium-128k-instruct) -- se pela cualquier texto antes del
        // primer '{' y después del último '}' antes de parsear.
        private static string LimpiarJson(string contenido)
        {
            int inicio = contenido.IndexOf('{');
            int fin = contenido.LastIndexOf('}');
            if (inicio < 0 || fin < inicio) return contenido;
            return contenido[inicio..(fin + 1)];
        }

        private static ExtraccionResultado ParsearRespuesta(string contenido)
        {
            try
            {
                using var doc = JsonDocument.Parse(LimpiarJson(contenido));
                var root = doc.RootElement;

                string titulo = root.TryGetProperty("titulo", out var t) ? t.GetString() ?? "" : "";
                string anio = root.TryGetProperty("anio", out var a) ? a.GetString() ?? "" : "";
                string editorial = root.TryGetProperty("editorial", out var ed) ? ed.GetString() ?? "" : "";

                var autores = new List<string>();
                if (root.TryGetProperty("autores_apa", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        string? s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) autores.Add(s.Trim());
                    }
                }

                bool exito = !string.IsNullOrWhiteSpace(titulo) || autores.Count > 0;

                return new ExtraccionResultado
                {
                    Titulo = titulo.Trim(),
                    AutoresApa = autores,
                    Anio = anio.Trim(),
                    Editorial = editorial.Trim(),
                    Exito = exito,
                };
            }
            catch (JsonException)
            {
                // El modelo no devolvió JSON válido pese al response_format:
                // se trata como extracción fallida, no como error fatal.
                return new ExtraccionResultado { Exito = false };
            }
        }
    }
}
