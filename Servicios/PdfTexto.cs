using System.Text;
using UglyToad.PdfPig;

namespace CitaPDF.Servicios
{
    // Extracción de texto plano de las primeras páginas con PdfPig -- el
    // título y los datos editoriales de un libro/artículo/capítulo casi
    // siempre están en la portada o la portadilla de copyright, así que no
    // hace falta leer el documento completo.
    public static class PdfTexto
    {
        private const int PaginasAExtraer = 3;

        // Umbral por debajo del cual se asume que el PDF no tiene capa de
        // texto (escaneado) -- no se rechaza el documento, el llamador debe
        // saltar directo al fallback manual.
        public const int TextoMinimoChars = 40;

        public static string ExtraerPrimerasPaginas(byte[] bytes)
        {
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            int n = Math.Min(PaginasAExtraer, pdf.NumberOfPages);

            foreach (var page in pdf.GetPages().Take(n))
                sb.AppendLine(page.Text);

            return sb.ToString().Trim();
        }
    }
}
