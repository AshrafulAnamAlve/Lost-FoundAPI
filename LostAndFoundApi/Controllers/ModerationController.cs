using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LostAndFoundApi.Controllers
{
    /// <summary>Allows signed-in users to report suspicious items or abusive users to admins.</summary>
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class ModerationController : ControllerBase
    {
        private readonly AppDbContext context;

        public ModerationController(AppDbContext context)
        {
            this.context = context;
        }

        private int Me => User.UserId();

        [HttpPost("Report")]
        public ActionResult SubmitReport(CreateUserReportRequest request)
        {
            if (request == null) return BadRequest(new { message = "Invalid report payload." });

            var targetType = (request.targetType ?? "").Trim().ToLowerInvariant();
            if (targetType is not ("item" or "user"))
                return BadRequest(new { message = "Report target must be 'item' or 'user'." });

            var reason = (request.reason ?? "").Trim();
            if (reason.Length < 3 || reason.Length > 100)
                return BadRequest(new { message = "Please choose or enter a reason between 3 and 100 characters." });

            var details = string.IsNullOrWhiteSpace(request.details) ? null : request.details.Trim();
            if (details is { Length: > 500 })
                return BadRequest(new { message = "Additional details must be under 500 characters." });

            string? itemKind = null;
            int? targetOwnerId = null;

            if (targetType == "item")
            {
                itemKind = (request.itemKind ?? "").Trim().ToLowerInvariant();
                if (itemKind is not ("lost" or "found"))
                    return BadRequest(new { message = "Item kind must be 'lost' or 'found'." });

                var ownerId = itemKind == "lost"
                    ? context.Losts.Where(l => l.id == request.targetId).Select(l => (int?)l.userId).FirstOrDefault()
                    : context.Founds.Where(f => f.id == request.targetId).Select(f => (int?)f.userId).FirstOrDefault();

                if (ownerId == null)
                    return NotFound(new { message = "That report no longer exists." });
                if (ownerId.Value == Me)
                    return BadRequest(new { message = "You cannot report your own post." });

                targetOwnerId = ownerId.Value;
            }
            else
            {
                var targetUser = context.Registers.Find(request.targetId);
                if (targetUser == null)
                    return NotFound(new { message = "That user no longer exists." });
                if (targetUser.id == Me)
                    return BadRequest(new { message = "You cannot report yourself." });
                if (string.Equals(targetUser.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { message = "Administrator accounts cannot be reported." });

                targetOwnerId = targetUser.id;
            }

            var duplicatePending = context.UserReports.Any(r =>
                r.ReporterUserId == Me &&
                r.TargetType == targetType &&
                r.TargetId == request.targetId &&
                r.TargetItemKind == itemKind &&
                r.Status == ModerationStatuses.Pending);

            if (duplicatePending)
                return BadRequest(new { message = "You have already submitted a pending report for this. An admin will review it soon." });

            var report = new UserReport
            {
                ReporterUserId = Me,
                TargetType = targetType,
                TargetId = request.targetId,
                TargetItemKind = itemKind,
                TargetOwnerUserId = targetOwnerId,
                Reason = reason,
                Details = details,
                Status = ModerationStatuses.Pending,
                CreatedAt = DateTime.UtcNow,
            };

            context.UserReports.Add(report);
            context.SaveChanges();

            return Ok(new
            {
                message = "Report submitted to administrators. Thank you.",
                id = report.Id,
            });
        }
    }
}
