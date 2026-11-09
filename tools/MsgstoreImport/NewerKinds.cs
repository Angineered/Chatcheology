namespace MsgstoreImport;

/// <summary>
/// How a msgstore <c>message_type</c> becomes a newer message (CH-71 D6, Angie 2026-11-08: a fixed placeholder for
/// what the archive cannot show yet, one wording per kind, never the item's content).
/// </summary>
/// <remarks>
/// <para><strong>Where the numbers come from.</strong> This repository knew only 9 = document (the documents rule,
/// confirmed on the real msgstore) and the census's 1 = image. The rest is the widely published reverse-engineered
/// meaning of the Android msgstore's <c>message_type</c> (0 text, 1 image, 2 audio, 3 video, 4 contact card,
/// 5 location, 7 system, 9 document, 13 GIF, 14 several contact cards, 15 deleted for everyone, 16 live location,
/// 20 sticker, 42/43/82 view-once image/video/voice, 66 poll, 90 call). It is not confirmed on the real msgstore:
/// <c>newer plan</c> prints every new message's type number next to the kind given to it, so Angie checks the
/// mapping against her phone before apply. A wrong mapping can only change which fixed wording a placeholder
/// shows; it never shows content.</para>
/// <para>Any other number is <see cref="Unknown"/>: "Message type not shown in this archive", counted by its
/// number. A media type without a <c>message_media</c> row, and a text type without text, are Unknown too.</para>
/// <para>Reactions are not message rows (they live in <c>message_add_on</c>): counted only, no row (D6).</para>
/// </remarks>
internal static class NewerKinds
{
    internal const long TextType = 0;
    internal const long SystemType = 7;

    /// <summary>Types shown as a user message with one attachment (the file, when a media record exists).</summary>
    internal static readonly HashSet<long> MediaTypes = [1, 2, 3, 9, 13, 20];

    internal const string Poll = "Poll";
    internal const string Location = "Location";
    internal const string Contact = "Contact";
    internal const string Call = "Call";
    internal const string ViewOnce = "ViewOnce";
    internal const string Deleted = "Deleted";
    internal const string Unknown = "Unknown";

    /// <summary>Types shown only as a placeholder, by kind.</summary>
    internal static readonly Dictionary<long, string> PlaceholderTypes = new()
    {
        [4] = Contact,
        [14] = Contact,
        [5] = Location,
        [16] = Location,
        [15] = Deleted,
        [42] = ViewOnce,
        [43] = ViewOnce,
        [82] = ViewOnce,
        [66] = Poll,
        [90] = Call,
    };

    /// <summary>The fixed wording of a system row. Never the row's own text.</summary>
    internal const string SystemWording = "System message (not shown in this archive)";

    /// <summary>The fixed wording of each placeholder kind.</summary>
    internal static string Wording(string kind) => kind switch
    {
        Poll => "Poll (not shown in this archive)",
        Location => "Location (not shown in this archive)",
        Contact => "Contact card (not shown in this archive)",
        Call => "Call (not shown in this archive)",
        ViewOnce => "View-once media (not shown in this archive)",
        Deleted => "Deleted message (not shown in this archive)",
        Unknown => "Message type not shown in this archive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static readonly string[] PlaceholderKindsInOrder = [Poll, Location, Contact, Call, ViewOnce, Deleted, Unknown];
}
