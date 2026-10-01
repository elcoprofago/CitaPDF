using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CitaPDF.Servicios;

namespace CitaPDF
{
    // Busca un archivo (llama-server.exe o modelos .gguf) en todas las
    // unidades y deja elegir uno. La búsqueda corre en segundo plano y se
    // cancela al cerrar la ventana.
    public partial class BuscarArchivoWindow : Window
    {
        private readonly CancellationTokenSource _cts = new();

        public string? RutaElegida { get; private set; }

        private sealed record Resultado(string Ruta, long Bytes)
        {
            public override string ToString() => $"{Ruta}    ({Bytes / 1024d / 1024 / 1024:0.0} GB)";
        }

        public BuscarArchivoWindow(string titulo, string patron, Func<string, bool>? filtro)
        {
            InitializeComponent();
            Title = titulo;
            Loaded += async (s, e) => await BuscarAsync(patron, filtro);
        }

        private async Task BuscarAsync(string patron, Func<string, bool>? filtro)
        {
            var progreso = new Progress<string>(dir => TxtEstado.Text = $"Buscando en {dir}");
            try
            {
                var rutas = await Task.Run(() => BuscadorArchivos.Buscar(patron, filtro, progreso, _cts.Token));
                var resultados = rutas
                    .Select(r => new Resultado(r, TamanioSeguro(r)))
                    .OrderBy(r => r.Ruta, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                LstResultados.ItemsSource = resultados;
                TxtEstado.Text = resultados.Count == 0
                    ? $"No se encontró ningún {patron} en las unidades disponibles."
                    : $"{resultados.Count} encontrado(s). Doble clic o \"Elegir\".";
            }
            catch (OperationCanceledException)
            {
                // Se cerró la ventana durante la búsqueda.
            }
        }

        private static long TamanioSeguro(string ruta)
        {
            try { return new FileInfo(ruta).Length; } catch { return 0; }
        }

        private void LstResultados_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            BtnElegir.IsEnabled = LstResultados.SelectedItem != null;
        }

        private void LstResultados_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstResultados.SelectedItem != null) Elegir();
        }

        private void BtnElegir_Click(object sender, RoutedEventArgs e) => Elegir();

        private void Elegir()
        {
            RutaElegida = ((Resultado)LstResultados.SelectedItem).Ruta;
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _cts.Cancel();
            base.OnClosing(e);
        }
    }
}
