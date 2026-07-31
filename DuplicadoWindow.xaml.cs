using System.Windows;
using CitaPDF.Servicios;

namespace CitaPDF
{
    public enum ResultadoDuplicado
    {
        Saltear,
        SaltearTodo,
        AgregarIgual,
    }

    // Diálogo de decisión cuando ProcesarUnoAsync detecta que un PDF ya está
    // catalogado (mismo hash). "Saltear todo" lo resuelve MainWindow: activa
    // un flag que evita volver a preguntar por el resto del lote actual.
    public partial class DuplicadoWindow : Window
    {
        public ResultadoDuplicado Resultado { get; private set; } = ResultadoDuplicado.Saltear;

        public DuplicadoWindow(string etiqueta, DocumentoRecord existente)
        {
            InitializeComponent();
            TxtMensaje.Text =
                $"{etiqueta} ya está catalogado como {existente.DocumentoId} ({existente.Titulo}).\n\n" +
                "¿Qué hacés con este documento?";
        }

        private void BtnAgregar_Click(object sender, RoutedEventArgs e)
        {
            Resultado = ResultadoDuplicado.AgregarIgual;
            Close();
        }

        private void BtnSaltear_Click(object sender, RoutedEventArgs e)
        {
            Resultado = ResultadoDuplicado.Saltear;
            Close();
        }

        private void BtnSaltearTodo_Click(object sender, RoutedEventArgs e)
        {
            Resultado = ResultadoDuplicado.SaltearTodo;
            Close();
        }
    }
}
