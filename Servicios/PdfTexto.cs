using System.Linq;
using System.Text;
using UglyToad.PdfPig;

namespace CitaPDF.Servicios
{
    // Extracción de texto plano de las primeras páginas con PdfPig -- el
    // título y los datos editoriales de un libro/artículo/capítulo casi
    // siempre están en la portada o la portadilla de copyright, así que no
    // hace falta leer el documento completo. En revistas/actas de congreso
    // la editorial a veces figura recién en una portadilla varias páginas
    // adentro, de ahí el margen hasta 10.
    public static class PdfTexto
    {
        private const int PaginasAExtraer = 10;

        // Umbral por debajo del cual se asume que el PDF no tiene capa de
        // texto (escaneado) -- no se rechaza el documento, el llamador debe
        // saltar directo al fallback manual.
        public const int TextoMinimoChars = 40;

        public static string ExtraerPrimerasPaginas(byte[] bytes)
        {
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            int n = Math.Min(PaginasAExtraer, pdf.NumberOfPages);

            // page.Text concatena las letras del PDF sin segmentar en
            // palabras -- en layouts a varias columnas (común en papers
            // académicos) eso pega palabras entre sí ("AttentionIsAllYou...")
            // y le complica la lectura al LLM. GetWords() ya viene segmentado
            // por palabra (NearestNeighbourWordExtractor), así que unirlas
            // con espacio da texto legible aunque se pierda el layout exacto
            // -- no hace falta para extraer título/autores/año/editorial.
            foreach (var page in pdf.GetPages().Take(n))
                sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));

            return sb.ToString().Trim();
        }
    }
}
