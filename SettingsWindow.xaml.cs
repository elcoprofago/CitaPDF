using System.IO;
using System.Windows;
using CitaPDF.Servicios;
using Microsoft.Win32;

namespace CitaPDF
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;

        // Con un lote en proceso no se unifica ni se actualiza: el lote está
        // escribiendo biblioteca.json y usando el modelo.
        private readonly bool _loteEnCurso;

        // Lo consulta MainWindow al cerrar (aunque se cancele la ventana):
        // si se unificó, hay que releer la biblioteca.
        public string? InformeUnificacion { get; private set; }

        public SettingsWindow(AppSettings settings, bool loteEnCurso = false)
        {
            InitializeComponent();
            _settings = settings;
            _loteEnCurso = loteEnCurso;
            TxtVersion.Text = $"Versión instalada: {Actualizador.VersionActual}";
            TxtFilasVisibles.Text = _settings.FilasVisiblesEnGrid.ToString();
            TxtCarpetaDatos.Text = Biblioteca.GetDataDir();

            switch (_settings.Tema)
            {
                case "Oscuro": RbTemaOscuro.IsChecked = true; break;
                case "Textura": RbTemaTextura.IsChecked = true; break;
                default: RbTemaClaro.IsChecked = true; break;
            }
            TxtRutaTextura.Text = _settings.RutaTextura ?? "";
            ActualizarEstadoTextura();

            // Rutas efectivas (las elegidas o las autodetectadas).
            TxtRutaServidor.Text = LlamaServerProceso.ServidorExe;
            TxtRutaModelo.Text = LlamaServerProceso.ModeloPath;
            ActualizarAvisoRutas();
        }

        // ===================== Modelo local =====================

        // null = el usuario no tocó esa ruta en esta ventana (se conserva lo
        // que hubiera en config.json, incluida la detección automática).
        private string? _servidorElegido;
        private string? _modeloElegido;

        private void BtnExaminarServidor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "llama-server.exe|llama-server.exe|Ejecutables (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) == true) ElegirServidor(dlg.FileName);
        }

        private void BtnBuscarServidor_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new BuscarArchivoWindow("Buscar llama-server.exe", "llama-server.exe", null) { Owner = this };
            if (wnd.ShowDialog() == true && wnd.RutaElegida != null) ElegirServidor(wnd.RutaElegida);
        }

        private void BtnExaminarModelo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Modelos GGUF (*.gguf)|*.gguf" };
            if (dlg.ShowDialog(this) == true) ElegirModelo(dlg.FileName);
        }

        private void BtnBuscarModelo_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new BuscarArchivoWindow("Buscar modelos (.gguf)", "*.gguf", BuscadorArchivos.EsModeloPrincipal) { Owner = this };
            if (wnd.ShowDialog() == true && wnd.RutaElegida != null) ElegirModelo(wnd.RutaElegida);
        }

        private void ElegirServidor(string ruta)
        {
            _servidorElegido = ruta;
            TxtRutaServidor.Text = ruta;
            ActualizarAvisoRutas();
        }

        private void ElegirModelo(string ruta)
        {
            if (!BuscadorArchivos.EsModeloPrincipal(ruta))
            {
                MessageBox.Show(this, "Ese archivo no es un modelo que se pueda cargar solo (proyector mmproj, vocabulario ggml-vocab o una parte 2..N de un modelo dividido). Elegí el modelo o su primera parte.",
                    "Archivo no válido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _modeloElegido = ruta;
            TxtRutaModelo.Text = ruta;
            ActualizarAvisoRutas();
        }

        private void ActualizarAvisoRutas()
        {
            var avisos = new List<string>();
            if (!File.Exists(TxtRutaServidor.Text)) avisos.Add("⚠ No existe el servidor elegido.");
            if (!File.Exists(TxtRutaModelo.Text)) avisos.Add("⚠ No existe el modelo elegido.");
            avisos.Add("Si están dentro de la carpeta del programa se guardan como ruta relativa (sirve en pendrive). Un cambio se aplica al próximo inicio del modelo.");
            TxtAvisoRutas.Text = string.Join("  ", avisos);
        }

        private void Tema_Changed(object sender, RoutedEventArgs e)
        {
            ActualizarEstadoTextura();
        }

        private void ActualizarEstadoTextura()
        {
            bool esTextura = RbTemaTextura.IsChecked == true;
            TxtRutaTextura.IsEnabled = esTextura;
            BtnExaminarTextura.IsEnabled = esTextura;
        }

        private void BtnExaminarTextura_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            };
            if (dlg.ShowDialog() == true)
                TxtRutaTextura.Text = dlg.FileName;
        }

        private void BtnGuardarCopia_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Guardar copia de la biblioteca",
                Filter = "Biblioteca CitaPDF (*.json)|*.json",
                FileName = $"biblioteca-{DateTime.Now:yyyy-MM-dd_HHmm}.json",
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                int cantidad = Biblioteca.ExportarCopia(dlg.FileName);
                MessageBox.Show(this, $"Copia guardada y verificada ({cantidad} documentos):\n{dlg.FileName}",
                    "Copia guardada", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo guardar la copia:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool AvisarSiLoteEnCurso()
        {
            if (!_loteEnCurso) return false;
            MessageBox.Show(this, "Hay un lote en proceso: esperá a que termine.", "Lote en curso",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        private void BtnUnificar_Click(object sender, RoutedEventArgs e)
        {
            if (AvisarSiLoteEnCurso()) return;
            var dlg = new OpenFileDialog
            {
                Title = "Elegí la otra biblioteca (no se va a modificar)",
                Filter = "Biblioteca CitaPDF (*.json)|*.json",
            };
            if (dlg.ShowDialog(this) != true) return;

            if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(Biblioteca.GetBibliotecaPath()), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Ése es el mismo biblioteca.json que está en uso. Elegí la otra copia.",
                    "Mismo archivo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UnificarWindow wnd;
            try
            {
                wnd = new UnificarWindow(dlg.FileName) { Owner = this };
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo leer alguna de las dos bibliotecas:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (wnd.ShowDialog() == true) InformeUnificacion = wnd.Informe;
        }

        private async void BtnActualizar_Click(object sender, RoutedEventArgs e)
        {
            if (AvisarSiLoteEnCurso()) return;
            BtnActualizar.IsEnabled = false;
            TxtEstadoActualizacion.Visibility = Visibility.Visible;
            var progreso = new Progress<string>(s => TxtEstadoActualizacion.Text = s);
            try
            {
                TxtEstadoActualizacion.Text = "Consultando GitHub...";
                var r = await Actualizador.ConsultarUltimaAsync();
                if (r.Version <= Actualizador.VersionActual)
                {
                    TxtEstadoActualizacion.Text = $"Ya tenés la última versión ({Actualizador.VersionActual}).";
                    return;
                }

                if (!Actualizador.EsEjecutablePublicado)
                {
                    TxtEstadoActualizacion.Text = $"Hay una versión nueva ({r.Version}), pero esta copia corre desde la compilación de Visual Studio: " +
                                                  $"la actualización automática es sólo para el CitaPDF.exe publicado. Descarga: {r.UrlPagina}";
                    return;
                }

                string notas = string.IsNullOrWhiteSpace(r.Notas) ? "" : $"\n\n{r.Notas.Trim()}";
                var ok = MessageBox.Show(this,
                    $"Hay una versión nueva: {r.Version} (instalada: {Actualizador.VersionActual}).{notas}\n\n" +
                    "¿Descargarla e instalarla? CitaPDF se va a reiniciar. La carpeta datos (biblioteca y configuración) no se toca, " +
                    "y la versión actual queda como CitaPDF.exe.anterior por si hay que volver atrás.\n\n" +
                    "Lo que no hayas guardado en esta ventana se pierde.",
                    "Actualizar CitaPDF", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ok != MessageBoxResult.Yes)
                {
                    TxtEstadoActualizacion.Text = $"Versión {r.Version} disponible (no instalada).";
                    return;
                }

                string exeNuevo = await Actualizador.DescargarYVerificarAsync(r, progreso);
                TxtEstadoActualizacion.Text = "Instalando...";
                string exeActual = Actualizador.ExeActual;
                Actualizador.Instalar(exeNuevo, exeActual);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exeActual)
                {
                    WorkingDirectory = Path.GetDirectoryName(exeActual)!,
                    UseShellExecute = false,
                });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                TxtEstadoActualizacion.Text = $"No se pudo actualizar: {ex.Message}";
            }
            finally
            {
                BtnActualizar.IsEnabled = true;
            }
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFilasVisibles.Text, out int filas) || filas < 3)
            {
                MessageBox.Show(this, "Ingresá un número entero de 3 o más.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string tema = RbTemaOscuro.IsChecked == true ? "Oscuro"
                : RbTemaTextura.IsChecked == true ? "Textura"
                : "Claro";

            if (tema == "Textura" && !File.Exists(TxtRutaTextura.Text))
            {
                MessageBox.Show(this, "Elegí una imagen válida para el tema de textura.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.FilasVisiblesEnGrid = filas;
            _settings.Tema = tema;
            _settings.RutaTextura = string.IsNullOrWhiteSpace(TxtRutaTextura.Text) ? null : TxtRutaTextura.Text;
            if (_servidorElegido != null) _settings.RutaServidor = Rutas.ParaGuardar(_servidorElegido);
            if (_modeloElegido != null) _settings.RutaModelo = Rutas.ParaGuardar(_modeloElegido);
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
