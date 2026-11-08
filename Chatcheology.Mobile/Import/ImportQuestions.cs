using System.Globalization;

namespace Chatcheology.Mobile.Import
{
    /// <summary>The phone owner's answer to the adoption question (CH-71 step 2).</summary>
    public enum AdoptionAnswer
    {
        /// <summary>Close the import; nothing is kept and nothing changes.</summary>
        Cancel,

        /// <summary>The new archive replaces the earlier copy and takes over its stars and settings.</summary>
        Replace,

        /// <summary>The new archive is installed beside the earlier copy, with nothing carried over.</summary>
        ImportAsNew,
    }

    /// <summary>
    /// The one-time adoption question (CH-71 step 2): a version 4 archive whose messages begin with exactly the messages
    /// of an archive installed before version 4 (which has no archive id) may replace it.
    /// </summary>
    /// <param name="ConversationTitle">The installed archive's title as the start screen shows it, or none.</param>
    /// <param name="InstalledMessages">How many messages the installed archive holds.</param>
    /// <param name="NewMessages">How many messages the new archive holds.</param>
    public sealed record AdoptionQuestion(string? ConversationTitle, int InstalledMessages, int NewMessages)
    {
        /// <summary>The dialog's title.</summary>
        public const string Heading = "Earlier copy found";

        /// <summary>The Replace button.</summary>
        public const string ReplaceButton = "Replace";

        /// <summary>The Import as new button.</summary>
        public const string ImportAsNewButton = "Import as new";

        /// <summary>The Cancel button.</summary>
        public const string CancelButton = "Cancel";

        /// <summary>The question: "Replace &lt;archive&gt; and keep its stars and settings?"</summary>
        public string Text => $"Replace {Name} and keep its stars and settings?";

        /// <summary>The line under the question: counts only.</summary>
        public string Detail =>
            $"The new archive holds all {InstalledMessages.ToString("N0", CultureInfo.CurrentCulture)} of its messages, unchanged, " +
            $"and {(NewMessages - InstalledMessages).ToString("N0", CultureInfo.CurrentCulture)} more. Replace removes the earlier copy; " +
            "Import as new keeps both.";

        /// <summary>The archive as the question names it.</summary>
        private string Name => string.IsNullOrWhiteSpace(ConversationTitle) ? "the earlier copy" : "“" + ConversationTitle + "”";

        /// <summary>The answer for a button the dialog returned (its text); anything else, Back included, is Cancel.</summary>
        public static AdoptionAnswer AnswerFor(string? button) => button switch
        {
            ReplaceButton => AdoptionAnswer.Replace,
            ImportAsNewButton => AdoptionAnswer.ImportAsNew,
            _ => AdoptionAnswer.Cancel,
        };
    }

    /// <summary>Asks the phone owner during an import (CH-71 step 2). Called on the import's worker thread.</summary>
    public interface IImportQuestions
    {
        /// <summary>Asks the adoption question and waits for the answer.</summary>
        AdoptionAnswer AskAdoption(AdoptionQuestion question, CancellationToken cancellationToken);
    }
}
