using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace CitaPDF.Servicios
{
    // Firma de PDF, hash de deduplicación y descarga por URL. Nunca escribe
    // los bytes a disco -- todo vive en memoria y se descarta después de
    // extraer texto/hash, ya que la app no copia ni conserva el archivo.
    public static class PdfValidacion
    {
        private const int TimeoutSegundos = 60;
        private const long MaxBytes = 200L * 1024 * 1024;
        private static readonly byte[] FirmaPdf = Encoding.ASCII.GetBytes("%PDF-");

        public static bool EsPdfValido(byte[] bytes)
        {
            if (bytes.Length < FirmaPdf.Length) return false;
            for (int i = 0; i < FirmaPdf.Length; i++)
                if (bytes[i] != FirmaPdf[i]) return false;
            return true;
        }

        public static string CalcularHashSha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // Acepta si el Content-Type declara application/pdf O si coinciden
        // los magic bytes -- basta con uno de los dos, mismo criterio que
        // _descargar_pdf en ingesta.py.
        public static async Task<byte[]> DescargarAsync(string url)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSegundos) };
            using var respuesta = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            respuesta.EnsureSuccessStatusCode();

            string? contentType = respuesta.Content.Headers.ContentType?.MediaType;

            await using var stream = await respuesta.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            byte[] bloque = new byte[64 * 1024];
            long total = 0;
            int leidos;
            while ((leidos = await stream.ReadAsync(bloque)) > 0)
            {
                total += leidos;
                if (total > MaxBytes)
                    throw new InvalidOperationException($"La descarga supera el tope de {MaxBytes / (1024 * 1024)} MB.");
                buffer.Write(bloque, 0, leidos);
            }

            byte[] bytes = buffer.ToArray();

            bool tipoOk = contentType != null && contentType.Contains("application/pdf", StringComparison.OrdinalIgnoreCase);
            bool firmaOk = EsPdfValido(bytes);
            if (!tipoOk && !firmaOk)
                throw new InvalidOperationException("El contenido descargado no parece ser un PDF (Content-Type y firma no coinciden).");

            return bytes;
        }
    }
}
