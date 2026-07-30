using System.Windows;
using CitaPDF.Servicios;

namespace CitaPDF
{
    public class BibliotecaRow
    {
        public DocumentoRecord Documento { get; }
        public BibliotecaRow(DocumentoRecord documento) => Documento = documento;

        public string Titulo => Documento.Titulo;
        public string Anio => Documento.Anio;
        public string Editorial => Documento.Editorial;
        public string AutoresTexto => string.Join("; ", Documento.AutoresApa);
        public string FechaTexto => Documento.FechaAdquisicion.ToString("yyyy-MM-dd");
        public string Estado => Documento.ExtraidoAutomaticamente
            ? "Guardado"
            : "Guardado (revisar datos)";
    }

    public partial class BibliotecaWindow : Window
    {
        private List<DocumentoRecord> _todos = new();

        public BibliotecaWindow()
        {
            InitializeComponent();
            _todos = Biblioteca.CargarDocumentos();
            AplicarFiltro();
        }

        private void AplicarFiltro()
        {
            string filtro = TxtBuscar.Text?.Trim() ?? "";
            IEnumerable<DocumentoRecord> filtrados = _todos;

            if (filtro.Length > 0)
            {
                filtrados = _todos.Where(d =>
                    d.Titulo.Contains(filtro, StringComparison.OrdinalIgnoreCase) ||
                    d.Editorial.Contains(filtro, StringComparison.OrdinalIgnoreCase) ||
                    d.Anio.Contains(filtro, StringComparison.OrdinalIgnoreCase) ||
                    d.AutoresApa.Any(a => a.Contains(filtro, StringComparison.OrdinalIgnoreCase)));
            }

            var filas = filtrados
                .OrderByDescending(d => d.FechaAdquisicion)
                .Select(d => new BibliotecaRow(d))
                .ToList();

            GridBiblioteca.ItemsSource = filas;
            TxtContador.Text = $"{filas.Count} de {_todos.Count}";
        }

        private void TxtBuscar_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            AplicarFiltro();
        }

        private void GridBiblioteca_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GridBiblioteca.SelectedItem is BibliotecaRow fila)
            {
                new CitacionWindow(fila.Documento) { Owner = this }.ShowDialog();
                _todos = Biblioteca.CargarDocumentos();
                AplicarFiltro();
            }
        }

        private void BtnCerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
