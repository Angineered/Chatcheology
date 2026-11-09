using Chatcheology.Mobile.Conversation;

namespace MsgstoreImport.Tests;

/// <summary>
/// CH-71 step 3b: the phone recognises a placeholder by its exact fixed wording (<see cref="MessageRow.NotShownWordings"/>),
/// so that list must be exactly the wordings <c>newer apply</c> writes for placeholders, and must not hold the system
/// row's wording (a system row is a System message, shown as a system line).
/// </summary>
public sealed class NewerWordingTests
{
    [Fact]
    public void The_phone_recognises_exactly_the_placeholder_wordings_the_import_writes()
    {
        var written = NewerKinds.PlaceholderKindsInOrder.Select(NewerKinds.Wording).ToHashSet(StringComparer.Ordinal);

        Assert.True(written.SetEquals(MessageRow.NotShownWordings));
        Assert.DoesNotContain(NewerKinds.SystemWording, MessageRow.NotShownWordings);

        // Every kind a msgstore type maps to has a wording the phone knows.
        Assert.All(NewerKinds.PlaceholderTypes.Values, kind => Assert.Contains(NewerKinds.Wording(kind), MessageRow.NotShownWordings));
    }

    [Fact]
    public void The_archive_export_accepts_exactly_the_placeholder_wordings_the_import_writes()
    {
        var written = NewerKinds.PlaceholderKindsInOrder.Select(NewerKinds.Wording).ToHashSet(StringComparer.Ordinal);

        Assert.True(written.SetEquals(Chatcheology.Data.ArchiveExport.NewerPlaceholderWordings.All));
        Assert.DoesNotContain(NewerKinds.SystemWording, Chatcheology.Data.ArchiveExport.NewerPlaceholderWordings.All);
    }

    [Theory]
    [InlineData(@"F:\", @"F:\Media\WhatsApp Images\IMG-1.jpg", true)]
    [InlineData(@"F:\", @"F:\", false)]
    [InlineData(@"F:\Phone", @"F:\Phone\Media\a.jpg", true)]
    [InlineData(@"F:\Phone", @"F:\PhoneOther\Media\a.jpg", false)]
    [InlineData(@"F:\Phone", @"F:\Phone", false)]
    [InlineData(@"F:\", @"G:\Media\a.jpg", false)]
    public void A_drive_root_media_folder_contains_its_files(string root, string full, bool beneath)
    {
        Assert.Equal(beneath, NewerPlanner.IsBeneathRoot(full, Path.TrimEndingDirectorySeparator(root)));
    }
}
