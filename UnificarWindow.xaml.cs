using System.IO;
using System.Windows;
using CitaPDF.Servicios;

namespace CitaPDF
{
    public partial class UnificarWindow : Window
    {
        public class ConflictoVista
        {
            private readonly Unificador.Par _par;
            public ConflictoVista(Unificador.Par par)
            {
                _par = par;
                string titulo = string.IsNullOrWhiteSpace(par.Local.Titulo) ? par.Otro.Titulo : par.Local.Titulo;
                Encabezado = par.Local.DocumentoId == par.Otro.DocumentoId
                    ? $"{par.Local.DocumentoId} — {titulo}"
                    : $"{par.Local.DocumentoId} (en la otra: {par.Otro.DocumentoId}) — {titulo}";
                TextoLocal = Describir("Esta biblioteca", par.Local, !par.SugerenciaEsOtro);
                TextoOtro = Describir("La otra", par.Otro, par.SugerenciaEsOtro);
            }

            public string Encabezado { get; }
            public string TextoLocal { get; }
            public string TextoOtro { get; }

            // El RadioButton que se desmarca empuja false: se ignora, manda
            // el que se marca.
            public bool ElegirLocal { get => !_par.ElegirOtro; set { if (value) _par.ElegirOtro = false; } }
            public bool ElegirOtro { get => _par.ElegirOtro; set { if (value) _par.ElegirOtro = true; } }

            private string Describir(string nombre, DocumentoRecord d, bool sugerida)
            {
                string fecha = d.FechaModificacion is DateTime f
                    ? f.ToString("dd/MM/yyyy HH:mm")
                    : $"sin registro (agregado el {d.FechaAdquisicion:dd/MM/yyyy})";
                var lineas = new List<string>
                {
                    nombre + (sugerida ? "  (sugerida)" : ""),
                    "Modificado: " + fecha,
                    $"Datos: {Unificador.CantidadDatos(d)} caracteres",
                };
                foreach (var c in _par.CamposEnConflicto)
                    lineas.Add($"{c}: {Unificador.Leer(d, c)}");
                return string.Join("\n", lineas);
            }
        }

        public class AgregadoVista
        {
            private readonly Unificador.Agregado _ag;
            public AgregadoVista(Unificador.Agregado ag) => _ag = ag;
            public bool Incluir { get => _ag.Incluir; set => _ag.Incluir = value; }
            public string Texto
            {
                get
                {
                    var d = _ag.Documento;
                    string autores = string.Join("; ", d.AutoresApa ?? new List<string>());
                    string titulo = string.IsNullOrWhiteSpace(d.Titulo) ? "(sin título)" : d.Titulo;
                    return $"{d.DocumentoId} — {titulo}" + (autores.Length > 0 ? $" — {autores}" : "") +
                           (string.IsNullOrWhiteSpace(d.Anio) ? "" : $" ({d.Anio})");
                }
            }
        }

        private readonly Unificador.Analisis _analisis;
        private readonly string _rutaOtra;

        // Resumen para el log de MainWindow (null si no se unificó).
        public string? Informe { get; private set; }

        public UnificarWindow(string rutaOtra)
        {
            InitializeComponent();
            _rutaOtra = rutaOtra;
            string rutaLocal = Biblioteca.GetBibliotecaPath();

            // Las dos lecturas lanzan si el archivo está dañado: el llamador
            // lo muestra y la ventana no se abre.
            var locales = File.Exists(rutaLocal) ? Biblioteca.LeerParaUnificar(rutaLocal) : new List<DocumentoRecord>();
            var otros = Biblioteca.LeerParaUnificar(rutaOtra);
            _analisis = Unificador.Analizar(locales, otros);

            TxtInfoLocal.Text = InfoArchivo(rutaLocal, locales.Count);
            TxtInfoOtra.Text = InfoArchivo(rutaOtra, otros.Count);

            var conflictos = _analisis.Conflictos.Select(p => new ConflictoVista(p)).ToList();
            var agregados = _analisis.Agregados.Select(a => new AgregadoVista(a)).ToList();
            var completados = _analisis.Completados.Select(p =>
            {
                var campos = new List<string>(p.CamposACompletar);
                if (p.CompletaRuta) campos.Add("ruta del PDF");
                return $"{p.Local.DocumentoId} — {p.Local.Titulo}: {string.Join(", ", campos)}";
            }).ToList();

            ListaConflictos.ItemsSource = conflictos;
            ListaAgregados.ItemsSource = agregados;
            ListaCompletados.ItemsSource = completados;
            ExpConflictos.Header = $"Conflictos a resolver ({conflictos.Count})";
            ExpAgregados.Header = $"Sólo en la otra: se agregan ({agregados.Count})";
            ExpCompletados.Header = $"Se completan campos vacíos ({completados.Count})";
            ExpConflictos.Visibility = conflictos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExpAgregados.Visibility = agregados.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExpCompletados.Visibility = completados.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            TxtResumen.Text =
                $"Iguales en ambas: {_analisis.Identicos}  ·  Sólo en ésta (se conservan): {_analisis.SoloLocales}  ·  " +
                $"Sólo en la otra: {agregados.Count}  ·  Con campos a completar: {completados.Count}  ·  Conflictos: {conflictos.Count}";

            if (conflictos.Count == 0 && agregados.Count == 0 && completados.Count == 0)
            {
                TxtResumen.Text += "\n\nNo hay nada para unificar: esta biblioteca ya contiene todo lo de la otra.";
                BtnUnificar.IsEnabled = false;
            }
        }

        private static string InfoArchivo(string ruta, int documentos)
        {
            if (!File.Exists(ruta)) return $"{ruta}\n(todavía no existe)";
            var fi = new FileInfo(ruta);
            return $"{ruta}\nModificada: {fi.LastWriteTime:dd/MM/yyyy HH:mm}  ·  Tamaño: {fi.Length / 1024.0:0.0} KB  ·  {documentos} documentos";
        }

        private void BtnUnificar_Click(object sender, RoutedEventArgs e)
        {
            string? respaldo = null;
            try
            {
                respaldo = Biblioteca.Respaldar("antes-de-unificar");
                var r = Unificador.Aplicar(_analisis);
                Biblioteca.GuardarDocumentosVerificado(r.Documentos);

                Informe = $"Biblioteca unificada con {_rutaOtra}: {r.Agregados} agregado(s), {r.Completados} completado(s), " +
                          $"{r.ConflictosResueltosConOtra} conflicto(s) resuelto(s) con la otra versión. Total: {r.Documentos.Count} documentos." +
                          (r.IdsRenumerados.Count > 0 ? $" IDs renumerados por estar ocupados: {string.Join(", ", r.IdsRenumerados)}." : "") +
                          (respaldo != null ? $" Respaldo previo: {respaldo}" : "");
                MessageBox.Show(this, Informe, "Unificación terminada", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"No se pudo unificar:\n{ex.Message}" +
                    (respaldo != null ? $"\n\nLa biblioteca tal como estaba antes quedó respaldada en:\n{respaldo}" : ""),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
