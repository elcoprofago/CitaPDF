using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CitaPDF.Servicios;

namespace CitaPDF
{
    // Envuelve un DocumentoRecord con propiedades de lectura/escritura --
    // el grid de biblioteca permite editar cualquier columna (los enlaces de
    // descarga en particular cambian con el tiempo), así que a diferencia de
    // DocumentoRow (MainWindow, sólo lectura) acá los setters escriben
    // directo sobre el registro subyacente.
    public class BibliotecaRow
    {
        public DocumentoRecord Documento { get; }
        public BibliotecaRow(DocumentoRecord documento) => Documento = documento;

        public string Titulo
        {
            get => Documento.Titulo;
            set => Documento.Titulo = value?.Trim() ?? "";
        }

        public string AutoresTexto
        {
            get => string.Join("; ", Documento.AutoresApa ?? new List<string>());
            set => Documento.AutoresApa = (value ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToList();
        }

        public string Anio
        {
            get => Documento.Anio;
            set => Documento.Anio = value?.Trim() ?? "";
        }

        public string Editorial
        {
            get => Documento.Editorial;
            set => Documento.Editorial = value?.Trim() ?? "";
        }

        public string Url
        {
            get => Documento.OrigenUrl ?? "";
            set => Documento.OrigenUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public partial class BibliotecaWindow : Window
    {
        private List<DocumentoRecord> _todos = new();

        // IDs editados/corregidos durante esta sesión de la ventana -- lo
        // consulta MainWindow al cerrar el diálogo para pasar esas filas de
        // "recién agregado" (rojo) a "corregido" (verde oscuro).
        public HashSet<string> IdsModificados { get; } = new();

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
            // El propio doble clic puede haber dejado la celda en edición
            // (el primer clic de los dos la abre) -- si no se confirma antes,
            // reasignar ItemsSource en AplicarFiltro() revienta con
            // "Sorting no permitido durante EditItem".
            if (GridBiblioteca.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true))
                GridBiblioteca.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            if (GridBiblioteca.SelectedItem is BibliotecaRow fila)
            {
                var ventana = new CitacionWindow(fila.Documento) { Owner = this };
                ventana.ShowDialog();
                if (ventana.SeGuardo) IdsModificados.Add(fila.Documento.DocumentoId);
                _todos = Biblioteca.CargarDocumentos();
                AplicarFiltro();
            }
        }

        private void GridBiblioteca_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != System.Windows.Controls.DataGridEditAction.Commit) return;
            if (e.Row.Item is not BibliotecaRow fila) return;
            if (e.EditingElement is not TextBox caja) return;

            // No depender de que el binding ya haya empujado el valor editado
            // al origen (CellEditEnding se dispara antes de eso y el momento
            // exacto no está garantizado) -- se lee directo del control.
            string valor = caja.Text;
            string header = e.Column.Header?.ToString() ?? "";
            string firmaAntes = Unificador.Firma(fila.Documento);
            switch (header)
            {
                case "Título": fila.Titulo = valor; break;
                case "Autores": fila.AutoresTexto = valor; break;
                case "Año": fila.Anio = valor; break;
                case "Editorial": fila.Editorial = valor; break;
                case "URL": fila.Url = valor; break;
                default: return;
            }

            // El año/título/autor/editorial sí se consideran parte de la
            // extracción automática; la URL es metadata aparte que suele
            // completarse después, así que no debe marcar la cita como
            // "para revisar".
            if (header != "URL") fila.Documento.ExtraidoAutomaticamente = false;
            if (Unificador.Firma(fila.Documento) != firmaAntes) fila.Documento.FechaModificacion = DateTime.Now;

            fila.Documento.CitaApa = CitaApa.Construir(
                fila.Documento.AutoresApa, fila.Documento.Anio, fila.Documento.Titulo,
                fila.Documento.Editorial, fila.Documento.OrigenUrl);

            Biblioteca.GuardarDocumentos(_todos);
            IdsModificados.Add(fila.Documento.DocumentoId);
        }

        // Reemplaza la columna "Adquirido" (tampoco resultaba de utilidad)
        // por un enlace directo al documento -- mismo criterio de apertura
        // que CitacionWindow / MainWindow. Si el PDF cambió de lugar, ofrece
        // ubicarlo (UbicarPdfWindow).
        private void LinkAbrirDocumento_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Hyperlink link || link.DataContext is not BibliotecaRow fila) return;
            UbicarPdfWindow.AbrirDocumento(this, fila.Documento, _todos, () => true);
        }

        private void MenuUbicarPdf_Click(object sender, RoutedEventArgs e)
        {
            if (GridBiblioteca.SelectedItem is not BibliotecaRow fila) return;
            new UbicarPdfWindow(fila.Documento, _todos) { Owner = this }.ShowDialog();
        }

        // El clic derecho no mueve la selección por defecto en un DataGrid
        // -- sin esto, "Borrar registro" podría borrar una fila distinta de
        // la que el usuario clickeó.
        private void GridBiblioteca_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dep = (DependencyObject)e.OriginalSource;
            while (dep != null && dep is not DataGridRow)
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is DataGridRow row) row.IsSelected = true;
        }

        private void MenuBorrarRegistro_Click(object sender, RoutedEventArgs e)
        {
            if (GridBiblioteca.SelectedItem is not BibliotecaRow fila) return;

            var confirmar = MessageBox.Show(this,
                $"¿Borrar definitivamente el registro {fila.Documento.DocumentoId} ({fila.Titulo})?\n\nEsta acción no se puede deshacer.",
                "Borrar registro", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmar != MessageBoxResult.Yes) return;

            _todos.RemoveAll(d => d.DocumentoId == fila.Documento.DocumentoId);
            Biblioteca.GuardarDocumentos(_todos);
            AplicarFiltro();
        }

        private void MenuCopiarCita_Click(object sender, RoutedEventArgs e)
        {
            if (GridBiblioteca.SelectedItem is BibliotecaRow fila && !string.IsNullOrWhiteSpace(fila.Documento.CitaApa))
                Clipboard.SetText(fila.Documento.CitaApa);
        }

        private void MenuCopiarFila_Click(object sender, RoutedEventArgs e)
        {
            if (GridBiblioteca.SelectedItem is BibliotecaRow fila)
            {
                string linea = string.Join("\t", fila.Titulo, fila.AutoresTexto, fila.Anio, fila.Editorial, fila.Url);
                Clipboard.SetText(linea);
            }
        }

        private void BtnCerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
