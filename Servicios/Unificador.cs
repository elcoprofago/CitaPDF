using System.Text.Json;

namespace CitaPDF.Servicios
{
    // Une la biblioteca en uso con otra copia (la de otra PC, la del
    // pendrive...) SUMANDO el contenido de ambas, nunca reemplazando una por
    // otra:
    //   - un documento que está sólo en la otra se agrega;
    //   - uno que está sólo en ésta se conserva tal cual;
    //   - si un mismo documento tiene un campo vacío de un lado y lleno del
    //     otro, se completa con el lleno;
    //   - sólo cuando el mismo campo tiene dos valores distintos no vacíos
    //     hay conflicto, y lo decide el usuario (UnificarWindow).
    // "Mismo documento" = mismo HashSha256 (el contenido del PDF), así que
    // no importa el nombre del archivo ni el DocumentoId, que cada copia
    // numera por su cuenta (CT-0120 puede ser un libro distinto en cada una).
    public static class Unificador
    {
        // Campos de la cita que se comparan. RutaArchivoOriginal queda
        // afuera a propósito: es la ruta del PDF en cada máquina, que se
        // espera distinta -- sólo se completa si acá está vacía.
        public static readonly string[] Campos = { "Título", "Autores", "Año", "Editorial", "URL" };

        public static string Leer(DocumentoRecord d, string campo) => campo switch
        {
            "Título" => d.Titulo ?? "",
            "Autores" => string.Join("; ", d.AutoresApa ?? new List<string>()),
            "Año" => d.Anio ?? "",
            "Editorial" => d.Editorial ?? "",
            "URL" => d.OrigenUrl ?? "",
            _ => throw new ArgumentException(campo),
        };

        private static void Copiar(DocumentoRecord destino, DocumentoRecord fuente, string campo)
        {
            switch (campo)
            {
                case "Título": destino.Titulo = fuente.Titulo; break;
                case "Autores": destino.AutoresApa = new List<string>(fuente.AutoresApa ?? new List<string>()); break;
                case "Año": destino.Anio = fuente.Anio; break;
                case "Editorial": destino.Editorial = fuente.Editorial; break;
                case "URL": destino.OrigenUrl = fuente.OrigenUrl; break;
            }
        }

        // Huella de los datos editables: si cambia, el registro se modificó
        // (BibliotecaWindow / CitacionWindow la comparan para fechar).
        public static string Firma(DocumentoRecord d) =>
            string.Join("\u001F", Campos.Select(c => Leer(d, c)));

        // "Cuánto dato tiene" una versión: caracteres en los campos de la cita.
        public static int CantidadDatos(DocumentoRecord d) =>
            Campos.Sum(c => Leer(d, c).Trim().Length);

        private static bool Vacio(string s) => string.IsNullOrWhiteSpace(s);

        private static bool Iguales(string a, string b) =>
            string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

        private static DocumentoRecord Clonar(DocumentoRecord d) =>
            JsonSerializer.Deserialize<DocumentoRecord>(JsonSerializer.Serialize(d))!;

        public class Par
        {
            public DocumentoRecord Local { get; init; } = null!;
            public DocumentoRecord Otro { get; init; } = null!;
            // Vacíos acá y llenos en la otra: se completan solos.
            public List<string> CamposACompletar { get; } = new();
            public bool CompletaRuta { get; set; }
            // Distintos y llenos en ambas: decide el usuario.
            public List<string> CamposEnConflicto { get; } = new();
            public bool EsConflicto => CamposEnConflicto.Count > 0;
            public bool EsIdentico => !EsConflicto && CamposACompletar.Count == 0 && !CompletaRuta;
            // Respuesta del usuario (o la sugerencia, si no la cambia).
            public bool ElegirOtro { get; set; }
            public bool SugerenciaEsOtro { get; set; }
        }

        public class Agregado
        {
            public DocumentoRecord Documento { get; init; } = null!;
            // Desmarcable: un documento que se borró a propósito en esta
            // copia sigue estando en la otra, y la unión lo traería de vuelta.
            public bool Incluir { get; set; } = true;
        }

        public class Analisis
        {
            public List<DocumentoRecord> Locales { get; init; } = new();
            public List<Par> Pares { get; } = new();
            public List<Agregado> Agregados { get; } = new();
            public int SoloLocales { get; set; }
            public IEnumerable<Par> Conflictos => Pares.Where(p => p.EsConflicto);
            public IEnumerable<Par> Completados => Pares.Where(p => !p.EsConflicto && !p.EsIdentico);
            public int Identicos => Pares.Count(p => p.EsIdentico);
        }

        // Sólo compara: no modifica ninguna de las dos listas.
        public static Analisis Analizar(List<DocumentoRecord> locales, List<DocumentoRecord> otros)
        {
            var a = new Analisis { Locales = locales };
            var emparejados = new HashSet<DocumentoRecord>(ReferenceEqualityComparer.Instance);
            var pendientes = new List<DocumentoRecord>();

            // 1ª pasada: mismo hash y mismo ID -- el caso normal de dos copias
            // que salieron de la misma biblioteca. Primero ésta, para que un
            // PDF agregado dos veces ("Agregar igual") se empareje con su
            // gemelo exacto y no con el otro duplicado.
            foreach (var o in otros)
            {
                var l = locales.FirstOrDefault(x => !emparejados.Contains(x) && x.DocumentoId == o.DocumentoId &&
                                                    x.HashSha256 == o.HashSha256);
                if (l != null) { emparejados.Add(l); a.Pares.Add(CrearPar(l, o)); }
                else pendientes.Add(o);
            }
            // 2ª pasada: mismo hash con distinto ID (dado de alta por separado
            // en cada copia). Un hash vacío no identifica nada: no empareja.
            foreach (var o in pendientes)
            {
                var l = Vacio(o.HashSha256) ? null
                    : locales.FirstOrDefault(x => !emparejados.Contains(x) && x.HashSha256 == o.HashSha256);
                if (l != null) { emparejados.Add(l); a.Pares.Add(CrearPar(l, o)); }
                else a.Agregados.Add(new Agregado { Documento = o });
            }
            a.SoloLocales = locales.Count - emparejados.Count;
            return a;
        }

        private static Par CrearPar(DocumentoRecord l, DocumentoRecord o)
        {
            var p = new Par { Local = l, Otro = o };
            foreach (var c in Campos)
            {
                string vl = Leer(l, c), vo = Leer(o, c);
                if (Iguales(vl, vo) || Vacio(vo)) continue;
                if (Vacio(vl)) p.CamposACompletar.Add(c);
                else p.CamposEnConflicto.Add(c);
            }
            p.CompletaRuta = Vacio(l.RutaArchivoOriginal ?? "") && !Vacio(o.RutaArchivoOriginal ?? "");

            // Sugerencia: la modificada más recientemente si ambas tienen
            // fecha; si no, la que tiene más datos cargados; empate: ésta.
            if (l.FechaModificacion is DateTime fl && o.FechaModificacion is DateTime fo)
                p.SugerenciaEsOtro = fo > fl;
            else
                p.SugerenciaEsOtro = CantidadDatos(o) > CantidadDatos(l);
            p.ElegirOtro = p.SugerenciaEsOtro;
            return p;
        }

        public class Resultado
        {
            public List<DocumentoRecord> Documentos { get; init; } = new();
            public int Agregados { get; set; }
            public int Completados { get; set; }
            public int ConflictosResueltosConOtra { get; set; }
            public List<string> IdsRenumerados { get; } = new();
        }

        // Arma la biblioteca unificada sobre COPIAS de los registros locales:
        // las listas analizadas quedan intactas (si algo falla después, no
        // hay nada a medio modificar en memoria).
        public static Resultado Aplicar(Analisis a)
        {
            var porLocal = a.Pares.ToDictionary(p => p.Local, p => p,
                (IEqualityComparer<DocumentoRecord>)ReferenceEqualityComparer.Instance);
            var docs = new List<DocumentoRecord>();
            var r = new Resultado { Documentos = docs };

            foreach (var original in a.Locales)
            {
                var d = Clonar(original);
                docs.Add(d);
                if (!porLocal.TryGetValue(original, out var p) || p.EsIdentico) continue;

                bool localSinCita = Vacio(original.Titulo) && (original.AutoresApa?.Count ?? 0) == 0;
                bool cambio = false;

                foreach (var c in p.CamposACompletar) { Copiar(d, p.Otro, c); cambio = true; }
                if (p.CompletaRuta) d.RutaArchivoOriginal = p.Otro.RutaArchivoOriginal;

                if (p.EsConflicto && p.ElegirOtro)
                {
                    foreach (var c in p.CamposEnConflicto) Copiar(d, p.Otro, c);
                    d.ExtraidoAutomaticamente = p.Otro.ExtraidoAutomaticamente;
                    cambio = true;
                    r.ConflictosResueltosConOtra++;
                }
                else if (p.CamposACompletar.Count > 0 && localSinCita)
                {
                    // Acá la extracción había fallado y la cita viene entera
                    // de la otra: su marca de "revisado" también.
                    d.ExtraidoAutomaticamente = p.Otro.ExtraidoAutomaticamente;
                }

                if (cambio)
                {
                    d.CitaApa = CitaApa.Construir(d.AutoresApa, d.Anio, d.Titulo, d.Editorial, d.OrigenUrl);
                    d.FechaModificacion = DateTime.Now;
                }
                if (p.CamposACompletar.Count > 0 || p.CompletaRuta) r.Completados++;
            }

            foreach (var ag in a.Agregados.Where(x => x.Incluir))
            {
                var d = Clonar(ag.Documento);
                // Cada copia numera sus IDs por su cuenta: si el número ya
                // está usado acá por otro documento, se le da uno nuevo.
                if (Vacio(d.DocumentoId) || docs.Any(x => x.DocumentoId == d.DocumentoId))
                {
                    string anterior = d.DocumentoId;
                    d.DocumentoId = Biblioteca.GenerarNuevoId(docs);
                    r.IdsRenumerados.Add($"{anterior} → {d.DocumentoId}");
                }
                docs.Add(d);
                r.Agregados++;
            }
            return r;
        }
    }
}
