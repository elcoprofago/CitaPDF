using System.IO;

namespace CitaPDF.Servicios
{
    // Rutas de la versión portable: todo lo que vive dentro de la carpeta del
    // .exe se guarda en config.json como ruta RELATIVA, para que siga
    // funcionando cuando el pendrive monta con otra letra de unidad. Lo que
    // está afuera (otro disco) se guarda absoluto, tal cual.
    public static class Rutas
    {
        public static string BaseDir => AppContext.BaseDirectory;

        // Ruta guardada en config.json -> ruta absoluta utilizable. Vacía si
        // no hay nada guardado.
        public static string Resolver(string? guardada)
        {
            if (string.IsNullOrWhiteSpace(guardada)) return "";
            // Path.Combine descarta BaseDir si "guardada" ya es absoluta.
            return Path.GetFullPath(Path.Combine(BaseDir, guardada));
        }

        // Ruta absoluta elegida por el usuario -> lo que se guarda en
        // config.json (relativa si está dentro de la carpeta del .exe).
        public static string? ParaGuardar(string? absoluta)
        {
            if (string.IsNullOrWhiteSpace(absoluta)) return null;
            string full = Path.GetFullPath(absoluta);
            string relativa = Path.GetRelativePath(BaseDir, full);
            bool dentro = !Path.IsPathRooted(relativa)
                && relativa != ".."
                && !relativa.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            return dentro ? relativa : full;
        }
    }
}
