using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LostAndFoundApi.Controllers
{
    /// <summary>Site-wide moderation, user blocking, item management, and analytics for administrators.</summary>
    [Authorize(Roles = "Admin")]
    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext context;
        private readonly IHostEnvironment environment;

        public AdminController(AppDbContext context, IHostEnvironment environment)
        {
            this.context = context;
            this.environment = environment;
        }

        private int Me => User.UserId();

        private bool CallerIsActiveAdmin() =>
            context.Registers.Any(r => r.id == Me && !r.IsBlocked && r.Role == "Admin");

        [HttpGet("Overview")]
        public ActionResult Overview()
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var users = context.Registers.Select(r => new { r.Role, r.IsBlocked }).ToList();
            var lost = context.Losts.Select(l => new { l.category, l.status }).ToList();
            var found = context.Founds.Select(f => new { f.category, f.status }).ToList();
            var reports = context.UserReports.Select(r => r.Status).ToList();

            var categoryMap = new Dictionary<string, (int lost, int found, int resolved)>(StringComparer.OrdinalIgnoreCase);
            void Accumulate(string? cat, bool isLost, string? status)
            {
                var key = string.IsNullOrWhiteSpace(cat) ? "other" : cat.Trim().ToLowerInvariant();
                categoryMap.TryGetValue(key, out var cur);
                var resolved = string.Equals(status, ItemStatus.Resolved, StringComparison.OrdinalIgnoreCase);
                categoryMap[key] = (
                    cur.lost + (isLost ? 1 : 0),
                    cur.found + (isLost ? 0 : 1),
                    cur.resolved + (resolved ? 1 : 0));
            }

            foreach (var l in lost) Accumulate(l.category, true, l.status);
            foreach (var f in found) Accumulate(f.category, false, f.status);

            var lostResolved = lost.Count(l => string.Equals(l.status, ItemStatus.Resolved, StringComparison.OrdinalIgnoreCase));
            var foundResolved = found.Count(f => string.Equals(f.status, ItemStatus.Resolved, StringComparison.OrdinalIgnoreCase));

            return Ok(new
            {
                users = new
                {
                    total = users.Count,
                    active = users.Count(u => !u.IsBlocked),
                    blocked = users.Count(u => u.IsBlocked),
                    admins = users.Count(u => string.Equals(u.Role, "Admin", StringComparison.OrdinalIgnoreCase)),
                },
                items = new
                {
                    total = lost.Count + found.Count,
                    lostTotal = lost.Count,
                    foundTotal = found.Count,
                    returnedLost = lostResolved,
                    handedOverFound = foundResolved,
                    totalResolved = lostResolved + foundResolved,
                    openTotal = (lost.Count - lostResolved) + (found.Count - foundResolved),
                },
                reports = new
                {
                    total = reports.Count,
                    pending = reports.Count(s => s == ModerationStatuses.Pending),
                    reviewed = reports.Count(s => s == ModerationStatuses.Reviewed),
                    dismissed = reports.Count(s => s == ModerationStatuses.Dismissed),
                },
                messagesCount = context.Messages.Count(),
                categories = categoryMap
                    .OrderByDescending(kv => kv.Value.lost + kv.Value.found)
                    .Select(kv => new
                    {
                        category = kv.Key,
                        lost = kv.Value.lost,
                        found = kv.Value.found,
                        total = kv.Value.lost + kv.Value.found,
                        resolved = kv.Value.resolved,
                    }),
            });
        }

        [HttpGet("Users")]
        public ActionResult Users()
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var lostStats = context.Losts
                .GroupBy(l => l.userId)
                .ToDictionary(g => g.Key, g => new
                {
                    total = g.Count(),
                    resolved = g.Count(x => x.status == ItemStatus.Resolved),
                });

            var foundStats = context.Founds
                .GroupBy(f => f.userId)
                .ToDictionary(g => g.Key, g => new
                {
                    total = g.Count(),
                    resolved = g.Count(x => x.status == ItemStatus.Resolved),
                });

            var reportsReceived = context.UserReports
                .Where(r => r.TargetOwnerUserId != null)
                .GroupBy(r => r.TargetOwnerUserId!.Value)
                .ToDictionary(g => g.Key, g => new
                {
                    total = g.Count(),
                    pending = g.Count(x => x.Status == ModerationStatuses.Pending),
                });

            var reportsSubmitted = context.UserReports
                .GroupBy(r => r.ReporterUserId)
                .ToDictionary(g => g.Key, g => g.Count());

            var msgCounts = context.Messages
                .GroupBy(m => m.senderId)
                .ToDictionary(g => g.Key, g => g.Count());

            var users = context.Registers
                .OrderByDescending(u => u.id)
                .ToList()
                .Select(u =>
                {
                    lostStats.TryGetValue(u.id, out var ls);
                    foundStats.TryGetValue(u.id, out var fs);
                    reportsReceived.TryGetValue(u.id, out var rr);
                    reportsSubmitted.TryGetValue(u.id, out var rs);
                    msgCounts.TryGetValue(u.id, out var mc);

                    var lostCount = ls?.total ?? 0;
                    var foundCount = fs?.total ?? 0;
                    var returnedLostCount = ls?.resolved ?? 0;
                    var handedOverFoundCount = fs?.resolved ?? 0;

                    return new
                    {
                        u.id,
                        firstName = u.FirstName,
                        lastName = u.LastName,
                        fullName = $"{u.FirstName} {u.LastName}".Trim(),
                        email = u.Email,
                        phone = u.Phone,
                        secondaryPhone = u.SecondaryPhone,
                        address = u.Address,
                        city = u.City,
                        u.imageUrl,
                        role = string.IsNullOrWhiteSpace(u.Role) ? "User" : u.Role,
                        isBlocked = u.IsBlocked,
                        blockedAt = u.BlockedAt,
                        blockReason = u.BlockReason,
                        createdAt = u.CreatedAt,
                        lostCount,
                        foundCount,
                        totalPosted = lostCount + foundCount,
                        returnedLostCount,
                        handedOverFoundCount,
                        totalResolved = returnedLostCount + handedOverFoundCount,
                        reportsReceivedCount = rr?.total ?? 0,
                        pendingReportsReceived = rr?.pending ?? 0,
                        reportsSubmittedCount = rs,
                        messagesSentCount = mc,
                    };
                });

            return Ok(users);
        }

        [HttpPost("Users/{id}/Block")]
        public ActionResult BlockUser(int id, BlockUserRequest? request)
        {
            if (!CallerIsActiveAdmin()) return Forbid();
            if (id == Me) return BadRequest(new { message = "You cannot block your own administrator account." });

            var user = context.Registers.Find(id);
            if (user == null) return NotFound(new { message = "User not found." });
            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Administrator accounts cannot be blocked." });

            var reason = string.IsNullOrWhiteSpace(request?.reason) ? null : request!.reason!.Trim();
            if (reason is { Length: > 250 }) reason = reason[..250];

            user.IsBlocked = true;
            user.BlockedAt = DateTime.UtcNow;
            user.BlockReason = reason;
            context.SaveChanges();

            return Ok(new
            {
                message = $"{user.FirstName} {user.LastName} has been blocked.",
                id = user.id,
                isBlocked = true,
                blockedAt = user.BlockedAt,
                blockReason = user.BlockReason,
            });
        }

        [HttpPost("Users/{id}/Unblock")]
        public ActionResult UnblockUser(int id)
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var user = context.Registers.Find(id);
            if (user == null) return NotFound(new { message = "User not found." });

            user.IsBlocked = false;
            user.BlockedAt = null;
            user.BlockReason = null;
            context.SaveChanges();

            return Ok(new
            {
                message = $"{user.FirstName} {user.LastName} has been unblocked.",
                id = user.id,
                isBlocked = false,
            });
        }

        [HttpGet("Items")]
        public ActionResult Items()
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var itemReportCounts = context.UserReports
                .Where(r => r.TargetType == "item" && r.TargetItemKind != null)
                .GroupBy(r => new { kind = r.TargetItemKind!, r.TargetId })
                .ToDictionary(
                    g => $"{g.Key.kind}:{g.Key.TargetId}",
                    g => new
                    {
                        total = g.Count(),
                        pending = g.Count(x => x.Status == ModerationStatuses.Pending),
                    });

            var blockedUsers = context.Registers
                .Where(u => u.IsBlocked)
                .Select(u => u.id)
                .ToHashSet();

            var lost = context.Losts.Select(l => new
            {
                l.id,
                type = "lost",
                l.itemName,
                l.category,
                l.location,
                l.description,
                date = l.dateLost,
                l.timeLost,
                l.brand,
                l.color,
                reward = l.Reward,
                l.userId,
                l.userName,
                l.email,
                l.phoneNumber,
                l.imageUrl,
                l.detectedCategory,
                l.detectedConfidence,
                status = l.status ?? ItemStatus.Open,
                l.resolvedAt,
                l.resolvedNote,
            }).ToList();

            var found = context.Founds.Select(f => new
            {
                f.id,
                type = "found",
                f.itemName,
                f.category,
                f.location,
                f.description,
                date = f.dateFound,
                f.timeLost,
                f.brand,
                f.color,
                reward = "",
                f.userId,
                f.userName,
                f.email,
                f.phoneNumber,
                f.imageUrl,
                f.detectedCategory,
                f.detectedConfidence,
                status = f.status ?? ItemStatus.Open,
                f.resolvedAt,
                f.resolvedNote,
            }).ToList();

            var combined = lost.Concat(found)
                .OrderByDescending(i => i.date)
                .ThenByDescending(i => i.id)
                .Select(i =>
                {
                    itemReportCounts.TryGetValue($"{i.type}:{i.id}", out var rc);
                    return new
                    {
                        i.id,
                        i.type,
                        i.itemName,
                        i.category,
                        i.location,
                        i.description,
                        i.date,
                        i.timeLost,
                        i.brand,
                        i.color,
                        i.reward,
                        i.userId,
                        i.userName,
                        i.email,
                        i.phoneNumber,
                        ownerBlocked = blockedUsers.Contains(i.userId),
                        i.imageUrl,
                        i.detectedCategory,
                        i.detectedConfidence,
                        i.status,
                        i.resolvedAt,
                        i.resolvedNote,
                        reportsCount = rc?.total ?? 0,
                        pendingReportsCount = rc?.pending ?? 0,
                    };
                });

            return Ok(combined);
        }

        [HttpDelete("Items/{type}/{id}")]
        public ActionResult DeleteItem(string type, int id)
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var normalized = (type ?? "").Trim().ToLowerInvariant();
            string? imageUrl;

            if (normalized == "lost")
            {
                var item = context.Losts.Find(id);
                if (item == null) return NotFound(new { message = "Lost item not found." });
                imageUrl = item.imageUrl;
                context.Losts.Remove(item);
            }
            else if (normalized == "found")
            {
                var item = context.Founds.Find(id);
                if (item == null) return NotFound(new { message = "Found item not found." });
                imageUrl = item.imageUrl;
                context.Founds.Remove(item);
            }
            else
            {
                return BadRequest(new { message = "Invalid type. Must be 'lost' or 'found'." });
            }

            // Close any pending moderation reports targeting this deleted item.
            var openReports = context.UserReports
                .Where(r => r.TargetType == "item" && r.TargetItemKind == normalized && r.TargetId == id && r.Status == ModerationStatuses.Pending)
                .ToList();
            foreach (var r in openReports)
            {
                r.Status = ModerationStatuses.Reviewed;
                r.ReviewedAt = DateTime.UtcNow;
                r.ReviewedByAdminId = Me;
                if (string.IsNullOrWhiteSpace(r.AdminNote)) r.AdminNote = "Item deleted by admin.";
            }

            context.SaveChanges();
            UploadedFileHelper.Delete(environment.ContentRootPath, imageUrl);

            return Ok(new { message = "Item deleted by administrator." });
        }

        [HttpGet("Reports")]
        public ActionResult Reports()
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var users = context.Registers.ToDictionary(
                u => u.id,
                u => new
                {
                    name = $"{u.FirstName} {u.LastName}".Trim(),
                    email = u.Email,
                    isBlocked = u.IsBlocked,
                });

            var lostItems = context.Losts.ToDictionary(
                l => l.id,
                l => new { l.itemName, l.category, l.imageUrl, l.status });

            var foundItems = context.Founds.ToDictionary(
                f => f.id,
                f => new { f.itemName, f.category, f.imageUrl, f.status });

            var rows = context.UserReports
                .OrderBy(r => r.Status == ModerationStatuses.Pending ? 0 : 1)
                .ThenByDescending(r => r.CreatedAt)
                .ToList()
                .Select(r =>
                {
                    users.TryGetValue(r.ReporterUserId, out var reporter);
                    object? ownerInfo = null;
                    bool targetUserBlocked = false;
                    if (r.TargetOwnerUserId is int oid && users.TryGetValue(oid, out var own))
                    {
                        targetUserBlocked = own.isBlocked;
                        ownerInfo = new { id = oid, own.name, own.email, own.isBlocked };
                    }

                    string targetTitle = r.TargetType == "user" ? "Account" : "Deleted item";
                    string? targetCategory = null;
                    string? targetImageUrl = null;
                    bool targetExists = false;

                    if (r.TargetType == "user" && users.TryGetValue(r.TargetId, out var tu))
                    {
                        targetTitle = tu.name;
                        targetExists = true;
                    }
                    else if (r.TargetType == "item" && r.TargetItemKind == "lost" && lostItems.TryGetValue(r.TargetId, out var li))
                    {
                        targetTitle = li.itemName;
                        targetCategory = li.category;
                        targetImageUrl = li.imageUrl;
                        targetExists = true;
                    }
                    else if (r.TargetType == "item" && r.TargetItemKind == "found" && foundItems.TryGetValue(r.TargetId, out var fi))
                    {
                        targetTitle = fi.itemName;
                        targetCategory = fi.category;
                        targetImageUrl = fi.imageUrl;
                        targetExists = true;
                    }

                    return new
                    {
                        id = r.Id,
                        targetType = r.TargetType,
                        targetId = r.TargetId,
                        itemKind = r.TargetItemKind,
                        targetTitle,
                        targetCategory,
                        targetImageUrl,
                        targetExists,
                        targetOwnerUserId = r.TargetOwnerUserId,
                        targetUserBlocked,
                        targetOwner = ownerInfo,
                        reporterUserId = r.ReporterUserId,
                        reporterName = reporter?.name ?? "Unknown user",
                        reporterEmail = reporter?.email ?? "",
                        reason = r.Reason,
                        details = r.Details,
                        status = r.Status,
                        createdAt = r.CreatedAt,
                        reviewedAt = r.ReviewedAt,
                        adminNote = r.AdminNote,
                    };
                });

            return Ok(rows);
        }

        [HttpPost("Reports/{id}/Resolve")]
        public ActionResult ResolveReport(int id, ResolveUserReportRequest request)
        {
            if (!CallerIsActiveAdmin()) return Forbid();

            var report = context.UserReports.Find(id);
            if (report == null) return NotFound(new { message = "Report not found." });

            var status = (request?.status ?? "").Trim().ToLowerInvariant();
            if (status is not (ModerationStatuses.Reviewed or ModerationStatuses.Dismissed))
                return BadRequest(new { message = "Status must be 'reviewed' or 'dismissed'." });

            var note = string.IsNullOrWhiteSpace(request?.adminNote) ? null : request!.adminNote!.Trim();
            if (note is { Length: > 400 }) note = note[..400];

            report.Status = status;
            report.ReviewedAt = DateTime.UtcNow;
            report.ReviewedByAdminId = Me;
            report.AdminNote = note;
            context.SaveChanges();

            return Ok(new { message = $"Report marked as {status}.", id = report.Id, status = report.Status });
        }
    }
}
