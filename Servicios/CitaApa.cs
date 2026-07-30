using System.Text;

namespace CitaPDF.Servicios
{
    // Armado determinístico de la cita APA-7 a partir de campos ya
    // extraídos (por el LLM o corregidos a mano) -- el LLM sólo extrae
    // datos, nunca redacta la cita él mismo, para que el formato sea
    // auditable y no dependa de que el modelo recuerde cada regla de
    // puntuación (ver plan de implementación).
    public static class CitaApa
    {
        public static string Construir(List<string> autoresApa, string anio, string titulo, string editorial, string? url)
        {
            string anioParte = string.IsNullOrWhiteSpace(anio) ? "s.f." : anio.Trim();
            string autoresParte = JuntarAutores(autoresApa);
            string tituloParte = string.IsNullOrWhiteSpace(titulo) ? "[Sin título]" : titulo.Trim();

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(autoresParte))
                sb.Append(autoresParte).Append(' ');
            sb.Append('(').Append(anioParte).Append("). ");
            sb.Append(tituloParte);
            if (!sb.ToString().TrimEnd().EndsWith('.')) sb.Append('.');

            if (!string.IsNullOrWhiteSpace(editorial))
            {
                sb.Append(' ').Append(editorial.Trim());
                if (!editorial.Trim().EndsWith('.')) sb.Append('.');
            }

            if (!string.IsNullOrWhiteSpace(url))
                sb.Append(" Recuperado de ").Append(url.Trim());

            return sb.ToString();
        }

        // Reglas de unión de autores APA-7 para los casos comunes (1-20
        // autores); no se optimiza el caso extremo de +20, poco frecuente en
        // una biblioteca personal.
        private static string JuntarAutores(List<string> autores)
        {
            var lista = autores.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
            return lista.Count switch
            {
                0 => "",
                1 => lista[0],
                2 => $"{lista[0]}, & {lista[1]}",
                _ => string.Join(", ", lista.Take(lista.Count - 1)) + $", & {lista[^1]}",
            };
        }
    }
}
