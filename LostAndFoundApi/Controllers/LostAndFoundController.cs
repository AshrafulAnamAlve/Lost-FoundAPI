using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;

namespace LostAndFoundApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LostAndFoundController : ControllerBase
    {
        private readonly AppDbContext context;
        private readonly IItemSimilarityService itemSimilarityService;
        private readonly IItemClassificationService itemClassificationService;
        private readonly AuthTokenService tokens;

        // Show at most this many suggestions, best first.
        private const int MaxSuggestions = 5;

        // Largest photo accepted, for items and profile pictures alike.
        private const long MaxImageBytes = 10 * 1024 * 1024;

        public LostAndFoundController(
            AppDbContext context,
            IItemSimilarityService itemSimilarityService,
            IItemClassificationService itemClassificationService,
            AuthTokenService tokens)
        {
            this.context = context;
            this.itemSimilarityService = itemSimilarityService;
            this.itemClassificationService = itemClassificationService;
            this.tokens = tokens;
        }

        /// <summary>The signed-in caller, from their token. 0 when anonymous.</summary>
        private int Me => User.UserId();

        private static object Problem(string message) => new { message };

        // Score every found item against a lost item, drop the owner's own posts and
        // anything below the match threshold, and return the best suggestions.
        private async Task<List<object>> FindFoundMatchesAsync(Lost lost)
        {
            var candidates = context.Founds
                .Where(f => (lost.userId == 0 || f.userId != lost.userId)
                         // An item already back with its owner is not a suggestion.
                         && (f.status == null || f.status != ItemStatus.Resolved))
                .ToList();

            // Fetch every embedding this pass needs in one batch. Without it the loop
            // below makes a network round trip per candidate, in series, and the user
            // waits through all of them.
            await itemSimilarityService.PrimeAsync(lost, candidates);

            var scored = new List<(Found item, ItemMatchResult result)>();
            foreach (var found in candidates)
            {
                var result = await itemSimilarityService.EvaluateLostFoundAsync(lost, found);
                scored.Add((found, result));
            }

            return scored
                .Where(x => x.result.Score >= ItemSimilarityService.MatchThreshold)
                .OrderByDescending(x => x.result.Score)
                .Take(MaxSuggestions)
                .Select(x => (object)new
                {
                    x.item.id,
                    x.item.itemName,
                    x.item.category,
                    x.item.description,
                    x.item.location,
                    x.item.brand,
                    x.item.color,
                    x.item.imageUrl,
                    x.item.userName,
                    x.item.phoneNumber,
                    x.item.email,
                    x.item.userId,
                    x.item.dateFound,
                    x.item.status,
                    matchPercent = Math.Round(x.result.Score * 100, 2),
                    confidence = x.result.Confidence,
                    matchReasons = x.result.Reasons
                })
                .ToList();
        }

        // Mirror of FindFoundMatchesAsync for the found -> lost direction.
        private async Task<List<object>> FindLostMatchesAsync(Found found)
        {
            var candidates = context.Losts
                .Where(l => (found.userId == 0 || l.userId != found.userId)
                         && (l.status == null || l.status != ItemStatus.Resolved))
                .ToList();

            await itemSimilarityService.PrimeAsync(found, candidates);

            var scored = new List<(Lost item, ItemMatchResult result)>();
            foreach (var lost in candidates)
            {
                var result = await itemSimilarityService.EvaluateLostFoundAsync(lost, found);
                scored.Add((lost, result));
            }

            return scored
                .Where(x => x.result.Score >= ItemSimilarityService.MatchThreshold)
                .OrderByDescending(x => x.result.Score)
                .Take(MaxSuggestions)
                .Select(x => (object)new
                {
                    x.item.id,
                    x.item.itemName,
                    x.item.category,
                    x.item.description,
                    x.item.location,
                    x.item.brand,
                    x.item.color,
                    x.item.imageUrl,
                    x.item.userName,
                    x.item.phoneNumber,
                    x.item.email,
                    x.item.userId,
                    x.item.dateLost,
                    x.item.status,
                    matchPercent = Math.Round(x.result.Score * 100, 2),
                    confidence = x.result.Confidence,
                    matchReasons = x.result.Reasons
                })
                .ToList();
        }

        // ── Accounts ───────────────────────────────────────────────────────────

        [HttpPost("Register")]
        public ActionResult Register(Register register)
        {
            if (register == null) return BadRequest("Invalid data.");

            register.FirstName = (register.FirstName ?? "").Trim();
            register.LastName = (register.LastName ?? "").Trim();
            register.Email = (register.Email ?? "").Trim();
            register.Phone = (register.Phone ?? "").Trim();
            register.SecondaryPhone = string.IsNullOrWhiteSpace(register.SecondaryPhone) ? null : register.SecondaryPhone.Trim();
            register.Address = (register.Address ?? "").Trim();
            register.City = (register.City ?? "").Trim();

            var error = InputRules.FirstError(
                InputRules.Name(register.FirstName, "First name"),
                InputRules.Name(register.LastName, "Last name"),
                InputRules.Email(register.Email),
                InputRules.Password(register.Password),
                InputRules.Phone(register.Phone, "Phone number", required: true),
                InputRules.Phone(register.SecondaryPhone, "Secondary phone", required: false),
                InputRules.Text(register.Address, "Address", 5, 120),
                InputRules.Text(register.City, "City", 2, 60));
            if (error is not null) return BadRequest(error);

            if (context.Registers.Any(r => r.Email == register.Email))
                return BadRequest("An account with this email already exists. Sign in instead.");

            register.id = 0;
            register.imageUrl = null;
            register.Role = "User";
            register.IsBlocked = false;
            register.BlockedAt = null;
            register.BlockReason = null;
            register.CreatedAt = DateTime.UtcNow;
            register.Password = PasswordSecurity.Hash(register, register.Password);

            context.Registers.Add(register);
            context.SaveChanges();
            return Ok("Registration successful");
        }

        [HttpPost("Login")]
        public ActionResult Login(Login login)
        {
            if (login == null) return BadRequest("Invalid data.");

            var email = (login.Email ?? "").Trim();
            var user = context.Registers.FirstOrDefault(r => r.Email == email);
            if (user == null || !PasswordSecurity.Verify(user, login.Password ?? "", out var upgraded))
                return Unauthorized("Invalid email or password.");

            if (upgraded is not null)
            {
                user.Password = upgraded;
                context.SaveChanges();
            }

            if (user.IsBlocked)
            {
                var msg = string.IsNullOrWhiteSpace(user.BlockReason)
                    ? "Your account has been blocked by an administrator."
                    : $"Your account has been blocked by an administrator: {user.BlockReason}";
                return StatusCode(403, Problem(msg));
            }

            var role = string.IsNullOrWhiteSpace(user.Role) ? "User" : user.Role;
            return Ok(new
            {
                message = "Login Successfull",
                userid = user.id,
                firstName = user.FirstName,
                lastName = user.LastName,
                role,
                token = tokens.CreateToken(user),
            });
        }

        // A user's own record (never the password), or just their public face for anyone else.
        [HttpGet("getUser/{id}")]
        public ActionResult getUser(int id)
        {
            var user = context.Registers.FirstOrDefault(u => u.id == id);
            if (user == null) return NotFound("User Not Found");

            if (Me == id) return Ok(OwnProfile(user));

            return Ok(new { user.id, firstName = user.FirstName, lastName = user.LastName, user.imageUrl });
        }

        private static object OwnProfile(Register user) => new
        {
            user.id,
            firstName = user.FirstName,
            lastName = user.LastName,
            email = user.Email,
            phone = user.Phone,
            secondaryPhone = user.SecondaryPhone,
            address = user.Address,
            city = user.City,
            user.imageUrl,
            role = string.IsNullOrWhiteSpace(user.Role) ? "User" : user.Role,
        };

        [Authorize]
        [HttpPut("UpdateUser")]
        public ActionResult UpdateUser(ProfileUpdate update)
        {
            var user = context.Registers.Find(Me);
            if (user == null) return Unauthorized();

            var firstName = (update.firstName ?? "").Trim();
            var lastName = (update.lastName ?? "").Trim();
            var email = (update.email ?? "").Trim();
            var phone = (update.phone ?? "").Trim();
            var secondary = string.IsNullOrWhiteSpace(update.secondaryPhone) ? null : update.secondaryPhone.Trim();
            var address = (update.address ?? "").Trim();
            var city = (update.city ?? "").Trim();

            var error = InputRules.FirstError(
                InputRules.Name(firstName, "First name"),
                InputRules.Name(lastName, "Last name"),
                InputRules.Email(email),
                InputRules.Phone(phone, "Phone number", required: true),
                InputRules.Phone(secondary, "Secondary phone", required: false),
                InputRules.Text(address, "Address", 5, 120),
                InputRules.Text(city, "City", 2, 60));
            if (error is not null) return BadRequest(Problem(error));

            if (context.Registers.Any(r => r.id != user.id && r.Email == email))
                return BadRequest(Problem("Another account already uses this email."));

            user.FirstName = firstName;
            user.LastName = lastName;
            user.Email = email;
            user.Phone = phone;
            user.SecondaryPhone = secondary;
            user.Address = address;
            user.City = city;

            // Every report shows its owner's contact details. They come from the
            // profile, so a changed number has to reach the reports already filed,
            // or finders keep calling the old one.
            var fullName = $"{firstName} {lastName}".Trim();
            foreach (var item in context.Losts.Where(l => l.userId == user.id))
            {
                item.userName = fullName; item.email = email; item.phoneNumber = phone; item.altContact = secondary;
            }
            foreach (var item in context.Founds.Where(f => f.userId == user.id))
            {
                item.userName = fullName; item.email = email; item.phoneNumber = phone; item.altContact = secondary;
            }

            context.SaveChanges();
            return Ok(new { message = "Profile updated", user = OwnProfile(user) });
        }

        [Authorize]
        [HttpPost("ChangePassword")]
        public ActionResult ChangePassword(ChangePasswordRequest request)
        {
            var user = context.Registers.Find(Me);
            if (user == null) return Unauthorized();

            if (!PasswordSecurity.Verify(user, request.currentPassword ?? "", out _))
                return BadRequest(Problem("Your current password is not correct."));

            var error = InputRules.Password(request.newPassword);
            if (error is not null) return BadRequest(Problem(error));

            user.Password = PasswordSecurity.Hash(user, request.newPassword!);
            context.SaveChanges();
            return Ok(new { message = "Password changed" });
        }

        // ── Reports ────────────────────────────────────────────────────────────

        // Contact details on a report are always the owner's profile, whatever the
        // request said: one person, one identity, one reachable number.
        private static void ApplyOwner(Register owner, Action<string, string, string, string?> set) =>
            set($"{owner.FirstName} {owner.LastName}".Trim(), owner.Email, owner.Phone, owner.SecondaryPhone);

        private static string? ValidateItem(string? itemName, string? category, string? location, string? description, DateTime date) =>
            InputRules.FirstError(
                InputRules.Text(itemName, "Item name", 3, 60),
                InputRules.Text(category, "Category", 2, 40),
                InputRules.Text(location, "Location", 3, 100),
                InputRules.Text(description, "Description", 15, 500),
                InputRules.ItemDate(date));

        [Authorize]
        [HttpPost("PostLost")]
        public async Task<ActionResult> PostLost(Lost lost)
        {
            if (lost == null) return BadRequest(Problem("Invalid data."));
            var owner = context.Registers.Find(Me);
            if (owner == null) return Unauthorized();

            var error = ValidateItem(lost.itemName, lost.category, lost.location, lost.description, lost.dateLost);
            if (error is not null) return BadRequest(Problem(error));

            lost.id = 0;
            lost.type = "lost";
            lost.userId = owner.id;
            lost.Reward = (lost.Reward ?? "").Trim();
            lost.imageUrl = null;
            lost.status = null; lost.resolvedAt = null; lost.resolvedNote = null;
            lost.resolvedWithItemId = null; lost.resolvedWithUserId = null;
            ApplyOwner(owner, (n, e, p, a) => { lost.userName = n; lost.email = e; lost.phoneNumber = p; lost.altContact = a; });

            context.Losts.Add(lost);
            context.SaveChanges();

            var suggestedMatches = await FindFoundMatchesAsync(lost);

            return Ok(new
            {
                message = "Lost item reported successfully",
                id = lost.id,
                suggestedMatches
            });
        }

        [Authorize]
        [HttpPost("PostFound")]
        public async Task<ActionResult> PostFound(Found found)
        {
            if (found == null) return BadRequest(Problem("Invalid data."));
            var owner = context.Registers.Find(Me);
            if (owner == null) return Unauthorized();

            var error = ValidateItem(found.itemName, found.category, found.location, found.description, found.dateFound);
            if (error is not null) return BadRequest(Problem(error));

            found.id = 0;
            found.type = "found";
            found.userId = owner.id;
            found.imageUrl = null;
            found.status = null; found.resolvedAt = null; found.resolvedNote = null;
            found.resolvedWithItemId = null; found.resolvedWithUserId = null;
            ApplyOwner(owner, (n, e, p, a) => { found.userName = n; found.email = e; found.phoneNumber = p; found.altContact = a; });

            context.Founds.Add(found);
            context.SaveChanges();

            var suggestedMatches = await FindLostMatchesAsync(found);

            return Ok(new
            {
                message = "Found item reported successfully",
                id = found.id,
                suggestedMatches
            });
        }

        // Edits a report. Only its owner may, and only the descriptive fields: who
        // filed it and how to reach them come from the profile. Matching is re-run
        // so the answer reflects the corrected description straight away.
        [Authorize]
        [HttpPut("UpdateItem/{type}/{id}")]
        public async Task<ActionResult> UpdateItem(string type, int id, ItemUpdate update)
        {
            if (update == null) return BadRequest(Problem("Invalid data."));
            var owner = context.Registers.Find(Me);
            if (owner == null) return Unauthorized();

            var date = update.date ?? DateTime.MinValue;
            var error = ValidateItem(update.itemName, update.category, update.location, update.description, date);
            if (error is not null) return BadRequest(Problem(error));

            string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            switch ((type ?? "").ToLower())
            {
                case "lost":
                {
                    var item = context.Losts.Find(id);
                    if (item == null) return NotFound(Problem("Lost item not found."));
                    if (item.userId != owner.id) return StatusCode(403, Problem("You can only edit your own reports."));

                    item.itemName = update.itemName!.Trim();
                    item.category = update.category!.Trim();
                    item.location = update.location!.Trim();
                    item.description = update.description!.Trim();
                    item.dateLost = date;
                    item.timeLost = Clean(update.timeLost);
                    item.brand = Clean(update.brand);
                    item.color = Clean(update.color);
                    item.Reward = (update.reward ?? "").Trim();
                    item.detectedCategory = Clean(update.detectedCategory);
                    item.detectedConfidence = item.detectedCategory is null ? null : update.detectedConfidence;
                    ApplyOwner(owner, (n, e, p, a) => { item.userName = n; item.email = e; item.phoneNumber = p; item.altContact = a; });
                    context.SaveChanges();

                    return Ok(new { message = "Report updated", id = item.id, suggestedMatches = await FindFoundMatchesAsync(item) });
                }
                case "found":
                {
                    var item = context.Founds.Find(id);
                    if (item == null) return NotFound(Problem("Found item not found."));
                    if (item.userId != owner.id) return StatusCode(403, Problem("You can only edit your own reports."));

                    item.itemName = update.itemName!.Trim();
                    item.category = update.category!.Trim();
                    item.location = update.location!.Trim();
                    item.description = update.description!.Trim();
                    item.dateFound = date;
                    item.timeLost = Clean(update.timeLost);
                    item.brand = Clean(update.brand);
                    item.color = Clean(update.color);
                    item.detectedCategory = Clean(update.detectedCategory);
                    item.detectedConfidence = item.detectedCategory is null ? null : update.detectedConfidence;
                    ApplyOwner(owner, (n, e, p, a) => { item.userName = n; item.email = e; item.phoneNumber = p; item.altContact = a; });
                    context.SaveChanges();

                    return Ok(new { message = "Report updated", id = item.id, suggestedMatches = await FindLostMatchesAsync(item) });
                }
                default:
                    return BadRequest(Problem("Invalid type. Must be 'lost' or 'found'."));
            }
        }

        // ── Photos ─────────────────────────────────────────────────────────────

        // Reads the upload's real format from its bytes - never from the file name or
        // the declared content type, both of which the sender controls. Anything that
        // is not one of these image formats is refused, so an upload can never be
        // served back as a web page.
        private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["JPEG"] = ".jpg", ["PNG"] = ".png", ["WEBP"] = ".webp", ["GIF"] = ".gif", ["BMP"] = ".bmp",
        };

        private static string? CheckImage(IFormFile? image, out string extension)
        {
            extension = "";
            if (image == null || image.Length == 0) return "No image provided.";
            if (image.Length > MaxImageBytes) return "The photo is larger than 10 MB. Please choose a smaller one.";

            try
            {
                using var stream = image.OpenReadStream();
                var format = Image.DetectFormat(stream);
                if (!ImageExtensions.TryGetValue(format.Name, out var ext))
                    return "Only JPG, PNG, WebP or GIF photos are accepted.";
                extension = ext;
                return null;
            }
            catch
            {
                return "That file is not a photo. Please choose a JPG, PNG, WebP or GIF image.";
            }
        }

        // Classifies a photo without storing anything, so the report form can show
        // what the model thinks while the user is still filling it in.
        //
        // This exists as its own endpoint because of the order the UI posts things:
        // PostLost/PostFound creates the item (and runs matching) first, and only
        // then does UploadImage attach the photo. By that point matching has already
        // run. Classifying up front lets the detected category travel with the form,
        // so the very first match pass can use it.
        [Authorize]
        [HttpPost("ClassifyImage")]
        public async Task<ActionResult> ClassifyImage(IFormFile image)
        {
            if (image == null || image.Length == 0)
                return BadRequest("No image provided.");
            if (image.Length > MaxImageBytes)
                return BadRequest("The photo is larger than 10 MB.");

            using var stream = image.OpenReadStream();
            var result = await itemClassificationService.ClassifyAsync(stream, image.FileName, HttpContext.RequestAborted);

            // A failure is reported in the body with 200, not as an error status: the
            // caller is a form that should carry on regardless, and treating "the
            // classifier is off" as a client-visible error would make it look broken.
            return Ok(new
            {
                label = result.Label,
                confidence = Math.Round(result.Confidence, 4),
                known = result.Known,
                category = result.Category,
                // The categories the form may keep without contradicting this photo, so
                // the client does not have to carry its own copy of the mapping and let
                // the two drift apart.
                acceptableCategories = ItemClassificationService.AcceptableCategoriesFor(result.Category),
                scores = result.Scores,
                available = result.Error is null,
                error = result.Error
            });
        }

        [Authorize]
        [HttpPost("UploadImage/{type}/{id}")]
        public async Task<ActionResult> UploadImage(string type, int id, IFormFile image)
        {
            type = (type ?? "").ToLower();
            if (type is not ("lost" or "found" or "user"))
                return BadRequest(Problem("Invalid type. Must be 'lost', 'found' or 'user'."));

            var imageError = CheckImage(image, out var extension);
            if (imageError is not null) return BadRequest(Problem(imageError));

            // Everything is checked before a byte is written, so a refused upload never
            // leaves an orphan file behind.
            Lost? lost = null; Found? found = null; Register? user = null;
            if (type == "lost")
            {
                lost = context.Losts.Find(id);
                if (lost == null) return NotFound(Problem("Lost item not found."));
                if (lost.userId != Me) return StatusCode(403, Problem("You can only change the photo on your own reports."));
            }
            else if (type == "found")
            {
                found = context.Founds.Find(id);
                if (found == null) return NotFound(Problem("Found item not found."));
                if (found.userId != Me) return StatusCode(403, Problem("You can only change the photo on your own reports."));
            }
            else
            {
                user = context.Registers.Find(id);
                if (user == null) return NotFound(Problem("User not found."));
                if (user.id != Me) return StatusCode(403, Problem("You can only change your own profile picture."));
            }

            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", type);
            Directory.CreateDirectory(uploadsFolder);

            var fileName = $"{Guid.NewGuid():N}{extension}";
            using (var fileStream = new FileStream(Path.Combine(uploadsFolder, fileName), FileMode.Create))
            {
                await image.CopyToAsync(fileStream);
            }

            var imageUrl = $"/uploads/{type}/{fileName}";
            string? replaced;

            if (lost is not null)
            {
                replaced = lost.imageUrl;
                lost.imageUrl = imageUrl;

                // Backstop. The report form classifies up front via ClassifyImage and
                // sends the answer with the item, which is what lets the first match
                // pass use it. This fills the gap for any client that did not, and
                // never overwrites a value that is already there.
                if (string.IsNullOrWhiteSpace(lost.detectedCategory))
                {
                    var detected = await ClassifyUploadAsync(image);
                    if (detected is not null)
                    {
                        lost.detectedCategory = detected.Value.Category;
                        lost.detectedConfidence = detected.Value.Confidence;
                    }
                }
            }
            else if (found is not null)
            {
                replaced = found.imageUrl;
                found.imageUrl = imageUrl;

                if (string.IsNullOrWhiteSpace(found.detectedCategory))
                {
                    var detected = await ClassifyUploadAsync(image);
                    if (detected is not null)
                    {
                        found.detectedCategory = detected.Value.Category;
                        found.detectedConfidence = detected.Value.Confidence;
                    }
                }
            }
            else
            {
                replaced = user!.imageUrl;
                user.imageUrl = imageUrl;
            }

            context.SaveChanges();
            if (!string.IsNullOrEmpty(replaced) && replaced != imageUrl) DeleteUploadedFile(replaced);

            return Ok(new { message = "Image uploaded successfully", imageUrl });
        }

        // ── Closing a report off ───────────────────────────────────────────────
        //
        // Two people who match talk to each other, one hands the item over, and
        // then each closes their own report: the owner marks their lost report
        // returned, the finder marks their found report handed over. Naming the
        // other person is optional — the matching engine is not always right, and
        // an item that came back through a friend or the front desk still has to
        // be closable.
        //
        // Only the person who filed a report can close it. Nobody gets to change
        // somebody else's record, which is also why closing one side deliberately
        // leaves the other side open for its own owner to confirm. Who is asking
        // comes from their sign-in token; the userId in the body is ignored.

        [Authorize]
        [HttpPost("MarkResolved")]
        public ActionResult MarkResolved(ResolveRequest request)
        {
            if (request == null) return BadRequest(new { message = "Invalid data." });
            request.userId = Me;

            var type = (request.type ?? "").ToLower();
            var note = string.IsNullOrWhiteSpace(request.note) ? null : request.note.Trim();
            if (note is { Length: > 200 }) note = note[..200];

            // When a counterpart report is named, believe the report over the
            // caller for whose it is.
            var counterpartUserId = request.counterpartUserId;
            if (request.counterpartItemId is int otherId)
            {
                counterpartUserId = type == "lost"
                    ? context.Founds.Find(otherId)?.userId ?? counterpartUserId
                    : context.Losts.Find(otherId)?.userId ?? counterpartUserId;
            }

            if (type == "lost")
            {
                var item = context.Losts.Find(request.id);
                if (item == null) return NotFound(new { message = "Lost item not found." });
                if (item.userId != request.userId)
                    return StatusCode(403, new { message = "Only the person who reported this item can close it." });

                item.status             = ItemStatus.Resolved;
                item.resolvedAt         = DateTime.UtcNow;
                item.resolvedWithUserId = counterpartUserId;
                item.resolvedWithItemId = request.counterpartItemId;
                item.resolvedNote       = note;
            }
            else if (type == "found")
            {
                var item = context.Founds.Find(request.id);
                if (item == null) return NotFound(new { message = "Found item not found." });
                if (item.userId != request.userId)
                    return StatusCode(403, new { message = "Only the person who reported this item can close it." });

                item.status             = ItemStatus.Resolved;
                item.resolvedAt         = DateTime.UtcNow;
                item.resolvedWithUserId = counterpartUserId;
                item.resolvedWithItemId = request.counterpartItemId;
                item.resolvedNote       = note;
            }
            else
            {
                return BadRequest(new { message = "Invalid type. Must be 'lost' or 'found'." });
            }

            context.SaveChanges();
            return Ok(new
            {
                message = type == "lost" ? "Marked as returned" : "Marked as handed over",
                status = ItemStatus.Resolved,
                resolvedAt = DateTime.UtcNow,
                resolvedWithUserId = counterpartUserId,
                resolvedWithItemId = request.counterpartItemId,
                resolvedNote = note,
            });
        }

        /// <summary>Undo: closing a report by mistake must not be permanent.</summary>
        [Authorize]
        [HttpPost("Reopen")]
        public ActionResult Reopen(ResolveRequest request)
        {
            if (request == null) return BadRequest(new { message = "Invalid data." });
            request.userId = Me;
            var type = (request.type ?? "").ToLower();

            if (type == "lost")
            {
                var item = context.Losts.Find(request.id);
                if (item == null) return NotFound(new { message = "Lost item not found." });
                if (item.userId != request.userId)
                    return StatusCode(403, new { message = "Only the person who reported this item can reopen it." });

                item.status = ItemStatus.Open;
                item.resolvedAt = null;
                item.resolvedWithUserId = null;
                item.resolvedWithItemId = null;
                item.resolvedNote = null;
            }
            else if (type == "found")
            {
                var item = context.Founds.Find(request.id);
                if (item == null) return NotFound(new { message = "Found item not found." });
                if (item.userId != request.userId)
                    return StatusCode(403, new { message = "Only the person who reported this item can reopen it." });

                item.status = ItemStatus.Open;
                item.resolvedAt = null;
                item.resolvedWithUserId = null;
                item.resolvedWithItemId = null;
                item.resolvedNote = null;
            }
            else
            {
                return BadRequest(new { message = "Invalid type. Must be 'lost' or 'found'." });
            }

            context.SaveChanges();
            return Ok(new { message = "Report reopened", status = ItemStatus.Open });
        }

        [Authorize]
        [HttpDelete("Delete/{type}/{id}")]
        public ActionResult Delete(string type, int id)
        {
            string? imageUrl;

            if (type.ToLower() == "lost")
            {
                var item = context.Losts.Find(id);
                if (item == null) return NotFound(Problem("Lost item not found."));
                if (item.userId != Me) return StatusCode(403, Problem("You can only delete your own reports."));
                imageUrl = item.imageUrl;
                context.Losts.Remove(item);
            }
            else if (type.ToLower() == "found")
            {
                var item = context.Founds.Find(id);
                if (item == null) return NotFound(Problem("Found item not found."));
                if (item.userId != Me) return StatusCode(403, Problem("You can only delete your own reports."));
                imageUrl = item.imageUrl;
                context.Founds.Remove(item);
            }
            else
            {
                return BadRequest(Problem("Invalid type. Must be 'lost' or 'found'."));
            }

            context.SaveChanges();
            DeleteUploadedFile(imageUrl);

            return Ok(new { message = "Item deleted successfully" });
        }

        // The detected app category for an upload, or null when the classifier is
        // switched off, unreachable, or simply not confident enough to commit.
        //
        // Only a confident answer is stored. An unsure guess is worse than nothing
        // here: it would be indistinguishable from a certain one later, when the
        // matching engine reads the column.
        private async Task<(string Category, double Confidence)?> ClassifyUploadAsync(IFormFile image)
        {
            if (!itemClassificationService.IsConfigured) return null;

            using var stream = image.OpenReadStream();
            var result = await itemClassificationService.ClassifyAsync(
                stream, image.FileName, HttpContext.RequestAborted);

            return result.Known && result.Category is not null
                ? (result.Category, result.Confidence)
                : null;
        }

        // Removes the physical upload backing an item's imageUrl (e.g. "/uploads/lost/xyz.jpg").
        private void DeleteUploadedFile(string? imageUrl) =>
            UploadedFileHelper.Delete(Directory.GetCurrentDirectory(), imageUrl);

        // ── Reading ────────────────────────────────────────────────────────────

        // The public list: what was reported, where and when - but not who reported it
        // or how to reach them. Anyone can browse; contact details need an account.
        [HttpGet("GetAllItem")]
        public ActionResult GetAllItem()
        {
            var lostItems = context.Losts.Select(l => new
            {
                l.id, l.type, l.itemName, l.category, l.location, l.description, l.dateLost, l.timeLost,
                l.brand, l.color, reward = l.Reward, l.userId, l.imageUrl, l.detectedCategory, l.detectedConfidence,
                l.status, l.resolvedAt,
            }).ToList();

            var foundItems = context.Founds.Select(f => new
            {
                f.id, f.type, f.itemName, f.category, f.location, f.description, f.dateFound, f.timeLost,
                f.brand, f.color, f.userId, f.imageUrl, f.detectedCategory, f.detectedConfidence,
                f.status, f.resolvedAt,
            }).ToList();

            return Ok(new
            {
                Lost = lostItems,
                Found = foundItems
            });
        }

        [Authorize]
        [HttpGet("GetItemById/{type}/{id}")]
        public ActionResult GetItemById(string type, int id)
        {
            if (type.ToLower() == "lost")
            {
                var item = context.Losts.FirstOrDefault(x => x.id == id);
                if (item == null) return NotFound(Problem("This report no longer exists."));
                return Ok(item);
            }
            if (type.ToLower() == "found")
            {
                var item = context.Founds.FirstOrDefault(x => x.id == id);
                if (item == null) return NotFound(Problem("This report no longer exists."));
                return Ok(item);
            }
            return BadRequest(Problem("Invalid type. Must be 'lost' or 'found'."));
        }

        [Authorize]
        [HttpGet("GetUserItems/{id}")]
        public ActionResult GetUserItems(int id)
        {
            if (id != Me) return StatusCode(403, Problem("You can only list your own reports."));

            var lostItems = context.Losts.Where(x => x.userId == id).ToList();
            var foundItems = context.Founds.Where(x => x.userId == id).ToList();

            return Ok(new
            {
                Lost = lostItems,
                Found = foundItems
            });
        }

        [Authorize]
        [HttpGet("GetMatchesForLost/{lostId}")]
        public async Task<ActionResult> GetMatchesForLost(int lostId)
        {
            var lost = context.Losts.FirstOrDefault(x => x.id == lostId);
            if (lost == null) return NotFound(Problem("Lost item not found."));

            var suggestedMatches = await FindFoundMatchesAsync(lost);

            return Ok(new
            {
                lostId,
                suggestedMatches
            });
        }

        [Authorize]
        [HttpGet("GetMatchesForFound/{foundId}")]
        public async Task<ActionResult> GetMatchesForFound(int foundId)
        {
            var found = context.Founds.FirstOrDefault(x => x.id == foundId);
            if (found == null) return NotFound(Problem("Found item not found."));

            var suggestedMatches = await FindLostMatchesAsync(found);

            return Ok(new
            {
                foundId,
                suggestedMatches
            });
        }

    }
}
