namespace LostAndFoundApi.Models
{
    /// <summary>
    /// The two states a report can be in. Stored as text so the column stays
    /// readable in the database, and so a null — every row filed before this
    /// existed — simply reads as open.
    /// </summary>
    public static class ItemStatus
    {
        public const string Open = "open";
        public const string Resolved = "resolved";

        /// <summary>Null and "open" mean the same thing; anything else is closed.</summary>
        public static bool IsResolved(string? status) => status == Resolved;
    }
}
