using System.ComponentModel.DataAnnotations;

namespace LostAndFoundApi.Models
{
    public static class ModerationStatuses
    {
        public const string Pending = "pending";
        public const string Reviewed = "reviewed";
        public const string Dismissed = "dismissed";
    }

    public class UserReport
    {
        [Key]
        public int Id { get; set; }

        public int ReporterUserId { get; set; }

        /// <summary>"item" or "user".</summary>
        public string TargetType { get; set; } = "";

        public int TargetId { get; set; }

        /// <summary>"lost" or "found" when TargetType is "item".</summary>
        public string? TargetItemKind { get; set; }

        /// <summary>Owner user ID for an item report, or TargetId for a user report.</summary>
        public int? TargetOwnerUserId { get; set; }

        public string Reason { get; set; } = "";
        public string? Details { get; set; }
        public string Status { get; set; } = ModerationStatuses.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReviewedAt { get; set; }
        public int? ReviewedByAdminId { get; set; }
        public string? AdminNote { get; set; }
    }

    public class CreateUserReportRequest
    {
        public string? targetType { get; set; }
        public int targetId { get; set; }
        public string? itemKind { get; set; }
        public string? reason { get; set; }
        public string? details { get; set; }
    }

    public class BlockUserRequest
    {
        public string? reason { get; set; }
    }

    public class ResolveUserReportRequest
    {
        public string? status { get; set; }
        public string? adminNote { get; set; }
    }
}
