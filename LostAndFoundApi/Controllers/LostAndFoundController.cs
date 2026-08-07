using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LostAndFoundApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LostAndFoundController : ControllerBase
    {
        private readonly AppDbContext context;
        private readonly IItemSimilarityService itemSimilarityService;

        // Show at most this many suggestions, best first.
        private const int MaxSuggestions = 5;

        public LostAndFoundController(AppDbContext context, IItemSimilarityService itemSimilarityService)
        {
            this.context = context;
            this.itemSimilarityService = itemSimilarityService;
        }

        // Score every found item against a lost item, drop the owner's own posts and
        // anything below the match threshold, and return the best suggestions.
        private async Task<List<object>> FindFoundMatchesAsync(Lost lost)
        {
            var candidates = context.Founds
                .Where(f => lost.userId == 0 || f.userId != lost.userId)
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
                .Where(l => found.userId == 0 || l.userId != found.userId)
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
                    matchPercent = Math.Round(x.result.Score * 100, 2),
                    confidence = x.result.Confidence,
                    matchReasons = x.result.Reasons
                })
                .ToList();
        }

        [HttpPost("Register")]
        public ActionResult Register(Register register)
        {
            if (register == null) return BadRequest("Invalid data.");
            bool emailExist = context.Registers.Any(r => r.Email == register.Email);
            if (emailExist) return BadRequest("Email already exists.");

            context.Registers.Add(register);
            context.SaveChanges();
            return Ok("Registration successful");
        }

        [HttpPost("Login")]
        public ActionResult Login(Login login)
        {
            if (login == null) return BadRequest("Invalid data.");
            var user = context.Registers.FirstOrDefault(r => r.Email == login.Email && r.Password == login.Password);
            if (user == null) return Unauthorized("Invalid email or password.");
            return Ok(new { message = "Login Successfull", userid = user.id });
        }

        [HttpPost("PostLost")]
        public async Task<ActionResult> PostLost(Lost lost)
        {
            if (lost == null) return BadRequest("Invalid data.");
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

        [HttpPost("PostFound")]
        public async Task<ActionResult> PostFound(Found found)
        {
            if (found == null) return BadRequest("Invalid data.");
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

        [HttpPost("UploadImage/{type}/{id}")]
        public async Task<ActionResult> UploadImage(string type, int id, IFormFile image)
        {
            if (image == null || image.Length == 0)
                return BadRequest("No image provided.");

            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", type.ToLower());
            Directory.CreateDirectory(uploadsFolder);

            var uniqueFileName = $"{Guid.NewGuid()}_{image.FileName}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await image.CopyToAsync(fileStream);
            }

            var imageUrl = $"/uploads/{type.ToLower()}/{uniqueFileName}";

            // Update database
            if (type.ToLower() == "lost")
            {
                var item = context.Losts.Find(id);
                if (item == null) return NotFound("Lost item not found.");
                item.imageUrl = imageUrl;
            }
            else if (type.ToLower() == "found")
            {
                var item = context.Founds.Find(id);
                if (item == null) return NotFound("Found item not found.");
                item.imageUrl = imageUrl;
            }
            else if (type.ToLower() == "user")
            {
                var user = context.Registers.Find(id);
                if (user == null) return NotFound("User not found.");
                user.imageUrl = imageUrl;
            }
            else
            {
                return BadRequest("Invalid type. Must be 'lost', 'found' or 'user'.");
            }

            context.SaveChanges();
            return Ok(new { message = "Image uploaded successfully", imageUrl });
        }

        [HttpDelete("Delete/{type}/{id}")]
        public ActionResult Delete(string type, int id)
        {
            string? imageUrl;

            if (type.ToLower() == "lost")
            {
                var item = context.Losts.Find(id);
                if (item == null) return NotFound("Lost item not found.");
                imageUrl = item.imageUrl;
                context.Losts.Remove(item);
            }
            else if (type.ToLower() == "found")
            {
                var item = context.Founds.Find(id);
                if (item == null) return NotFound("Found item not found.");
                imageUrl = item.imageUrl;
                context.Founds.Remove(item);
            }
            else
            {
                return BadRequest("Invalid type. Must be 'lost' or 'found'.");
            }

            context.SaveChanges();
            DeleteUploadedFile(imageUrl);

            return Ok(new { message = "Item deleted successfully" });
        }

        // Removes the physical upload backing an item's imageUrl (e.g. "/uploads/lost/xyz.jpg").
        private void DeleteUploadedFile(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.StartsWith("/uploads/")) return;

            try
            {
                var relativePath = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relativePath);
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            }
            catch
            {
                // A leftover file is harmless; never fail the delete because of it.
            }
        }

        [HttpGet("GetAllItem")]
        public ActionResult GetAllItem()
        {
            var lostItems = context.Losts.ToList();
            var foundItems = context.Founds.ToList();

            return Ok(new
            {
                Lost = lostItems,
                Found = foundItems
            });
        }

        [HttpGet("getUser/{id}")]
        public ActionResult getUser(int id)
        {
            var user = context.Registers.FirstOrDefault(u => u.id == id);
            if (user == null) return NotFound("User Not Found");
            return Ok(user);
        }

        [HttpGet("GetItemById/{type}/{id}")]
        public ActionResult GetItemById(string type, int id)
        {
            if (type.ToLower() == "lost")
            {
                var item = context.Losts.FirstOrDefault(x => x.id == id);
                if (item == null) return NotFound();
                return Ok(item);
            }
            if (type.ToLower() == "found")
            {
                var item = context.Founds.FirstOrDefault(x => x.id == id);
                if (item == null) return NotFound();
                return Ok(item);
            }
            return BadRequest("Invalid type. Must be 'lost' or 'found'.");
        }

        [HttpGet("GetUserItems/{id}")]
        public ActionResult GetUserItems(int id)
        {
            var lostItems = context.Losts.Where(x => x.userId == id).ToList();
            var foundItems = context.Founds.Where(x => x.userId == id).ToList();

            return Ok(new
            {
                Lost = lostItems,
                Found = foundItems
            });
        }

        [HttpGet("GetMatchesForLost/{lostId}")]
        public async Task<ActionResult> GetMatchesForLost(int lostId)
        {
            var lost = context.Losts.FirstOrDefault(x => x.id == lostId);
            if (lost == null) return NotFound("Lost item not found.");

            var suggestedMatches = await FindFoundMatchesAsync(lost);

            return Ok(new
            {
                lostId,
                suggestedMatches
            });
        }

        [HttpGet("GetMatchesForFound/{foundId}")]
        public async Task<ActionResult> GetMatchesForFound(int foundId)
        {
            var found = context.Founds.FirstOrDefault(x => x.id == foundId);
            if (found == null) return NotFound("Found item not found.");

            var suggestedMatches = await FindLostMatchesAsync(found);

            return Ok(new
            {
                foundId,
                suggestedMatches
            });
        }

    }
}
