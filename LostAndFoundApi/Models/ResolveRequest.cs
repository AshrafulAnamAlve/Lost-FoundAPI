namespace LostAndFoundApi.Models
{
    /// <summary>
    /// Closing a report off: the owner says the item is back where it belongs.
    /// </summary>
    public class ResolveRequest
    {
        /// <summary>"lost" or "found".</summary>
        public string type { get; set; } = "";

        /// <summary>The report being closed.</summary>
        public int id { get; set; }

        /// <summary>Who is asking. Only the person who filed the report may close it.</summary>
        public int userId { get; set; }

        // The other side of the handover, when the user names one. Both are
        // optional on purpose: an item can come back without the matching engine
        // ever getting it right, and a person who cannot name a counterpart must
        // still be able to close their report.
        public int? counterpartUserId { get; set; }
        public int? counterpartItemId { get; set; }

        /// <summary>Optional free note, e.g. "handed over at the front desk".</summary>
        public string? note { get; set; }
    }
}
