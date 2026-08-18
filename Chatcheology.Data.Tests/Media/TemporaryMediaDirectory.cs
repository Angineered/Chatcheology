using System.Diagnostics;
using System.Text;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// An isolated synthetic media tree in its own temporary directory, removed on dispose.
    /// </summary>
    /// <remarks>
    /// Each instance gets a private directory under the system temporary path, well outside the
    /// repository working tree, so a test run can never leave media files among the source. Every
    /// file it creates is invented here: no real media, file name, path or personal content is
    /// referenced anywhere in this project.
    /// <para>
    /// Content is written from short ASCII strings rather than random bytes, so a test that expects
    /// two files to deduplicate can say so by giving them the same string and the expectation is
    /// visible in the test itself.
    /// </para>
    /// </remarks>
    internal sealed class TemporaryMediaDirectory : IDisposable
    {
        private readonly string _containerPath;

        internal TemporaryMediaDirectory()
        {
            _containerPath = Path.Combine(
                Path.GetTempPath(),
                "Chatcheology.Data.Tests.Media",
                Guid.NewGuid().ToString("N"));

            RootPath = Path.Combine(_containerPath, "Media");

            Directory.CreateDirectory(RootPath);
        }

        /// <summary>The media root to hand to the inventory service.</summary>
        internal string RootPath { get; }

        /// <summary>
        /// The directory containing the root, for tests that need somewhere outside the tree.
        /// </summary>
        internal string ContainerPath => _containerPath;

        /// <summary>
        /// Creates a file at <paramref name="relativePath"/> beneath the root, holding
        /// <paramref name="content"/>.
        /// </summary>
        /// <param name="relativePath">
        /// A <c>/</c>-separated path relative to the root, so a test reads the same way the
        /// workspace stores it.
        /// </param>
        /// <returns>The full physical path of the created file.</returns>
        internal string CreateFile(string relativePath, string content = "content")
        {
            var fullPath = ResolveRelative(relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // ASCII, so a file's byte length is its string length and a test can state an expected
            // size without counting encoding overhead.
            File.WriteAllBytes(fullPath, Encoding.ASCII.GetBytes(content));

            return fullPath;
        }

        /// <summary>Creates an empty file at <paramref name="relativePath"/>.</summary>
        internal string CreateEmptyFile(string relativePath) => CreateFile(relativePath, string.Empty);

        /// <summary>The full physical path of <paramref name="relativePath"/> beneath the root.</summary>
        internal string ResolveRelative(string relativePath) =>
            Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Creates a directory junction at <paramref name="relativePath"/> pointing at
        /// <paramref name="targetPath"/>, if this machine allows it.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the junction was created. <see langword="false"/> if it could
        /// not be, which is a reason to skip a test rather than to fail one.
        /// </returns>
        /// <remarks>
        /// A junction rather than a symbolic link, deliberately: both are reparse points and both
        /// exercise the enumeration rule, but creating a symbolic link needs administrator rights
        /// or developer mode, and a test suite that only proves its point on an elevated machine
        /// proves it where it is least needed.
        /// <para>
        /// Created by <c>mklink /J</c> because the base class library exposes no junction API.
        /// </para>
        /// </remarks>
        internal bool TryCreateJunction(string relativePath, string targetPath)
        {
            var linkPath = ResolveRelative(relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = Process.Start(startInfo);

                if (process is null)
                {
                    return false;
                }

                process.WaitForExit();

                return process.ExitCode == 0 && Directory.Exists(linkPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <remarks>
        /// Junctions are removed first, by a walk that stops at each one rather than descending
        /// through it. Neither the search for them nor the deletion of the tree may ever reach into
        /// the directory a junction points at: a test fixture capable of deleting somewhere else on
        /// the disk would be a worse problem than any test it enabled.
        /// </remarks>
        public void Dispose()
        {
            RemoveJunctions(RootPath);

            Directory.Delete(_containerPath, recursive: true);
        }

        /// <summary>
        /// Deletes every junction beneath <paramref name="directoryPath"/>, without following one.
        /// </summary>
        /// <remarks>
        /// Written as an explicit walk rather than as
        /// <see cref="SearchOption.AllDirectories"/>, which recurses through reparse points and
        /// would therefore go looking inside the very directories this must not enter.
        /// </remarks>
        private static void RemoveJunctions(string directoryPath)
        {
            foreach (var child in Directory.EnumerateDirectories(directoryPath))
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    // Deletes the link, never its target.
                    Directory.Delete(child);
                    continue;
                }

                RemoveJunctions(child);
            }
        }
    }
}
