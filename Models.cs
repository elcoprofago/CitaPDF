namespace CitaPDF
{
    // Un registro de biblioteca: sólo metadata + cita, nunca una copia del
    // PDF (ver plan de implementación, corrección explícita del usuario).
    public class DocumentoRecord
    {
        public string DocumentoId { get; set; } = "";
        public string Titulo { get; set; } = "";
        public List<string> AutoresApa { get; set; } = new();
        public string Anio { get; set; } = "";
        public string Editorial { get; set; } = "";

        // Exactamente uno de los dos identifica el origen: archivo local
        // (ruta absoluta, sin copiar el PDF a ningún lado) o URL de descarga.
        public string? OrigenUrl { get; set; }
        public string? RutaArchivoOriginal { get; set; }

        public string HashSha256 { get; set; } = "";
        public DateTime FechaAdquisicion { get; set; }
        public string CitaApa { get; set; } = "";

        // false cuando el pipeline automático no pudo completar algo y el
        // usuario tuvo que corregir/completar campos a mano.
        public bool ExtraidoAutomaticamente { get; set; }
    }

    public class BibliotecaFile
    {
        public int Version { get; set; } = 1;
        public List<DocumentoRecord> Documentos { get; set; } = new();
    }

    public class AppSettings
    {
        public int Version { get; set; } = 1;

        // Cuántas filas recientes se muestran en el grid principal sin abrir
        // la biblioteca completa -- "al menos las últimas 3" del pedido
        // original, configurable desde SettingsWindow.
        public int FilasVisiblesEnGrid { get; set; } = 15;
    }
}
