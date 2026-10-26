using System.Text;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.OlderExports
{
    /// <summary>
    /// A private temporary folder holding synthetic export folders and a workspace folder, removed
    /// on dispose.
    /// </summary>
    /// <remarks>
    /// Every export folder gets a chat file of the same name, as real exports of one chat do.
    /// All names, words and bytes are invented.
    /// </remarks>
    internal sealed class SyntheticExportFolders : IDisposable
    {
        internal const string ChatFileName = "WhatsApp Chat with Sam Sample.txt";

        internal SyntheticExportFolders()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(), "Chatcheology.Data.Tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(RootPath);

            WorkspaceFolder = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspaceFolder);
        }

        internal string RootPath { get; }

        internal string WorkspaceFolder { get; }

        internal string OlderExportsPath =>
            Path.Combine(WorkspaceFolder, Chatcheology.Data.OlderExports.OlderExportsDatabase.FileName);

        /// <summary>Creates an export folder with the chat text and the given media files.</summary>
        internal string CreateExport(string folderName, string chatText, params (string Name, string Bytes)[] media)
        {
            var folder = Path.Combine(RootPath, folderName);
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, ChatFileName), chatText, new UTF8Encoding(false));

            foreach (var (name, bytes) in media)
            {
                File.WriteAllText(Path.Combine(folder, name), bytes, new UTF8Encoding(false));
            }

            return folder;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            Directory.Delete(RootPath, recursive: true);
        }
    }
}
