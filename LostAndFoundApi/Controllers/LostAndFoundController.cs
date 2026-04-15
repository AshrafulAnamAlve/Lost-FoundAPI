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

        public LostAndFoundController(AppDbContext context, IItemSimilarityService itemSimilarityService)
        {
            this.context = context;
            this.itemSimilarityService = itemSimilarityService;
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

            var foundItems = context.Founds.ToList();
            var scoredMatches = new List<(Found item, double score)>();

            foreach (var found in foundItems)
            {
                var score = await itemSimilarityService.CalculateLostFoundScoreAsync(lost, found);
                scoredMatches.Add((found, score));
            }

            var suggestedMatches = scoredMatches
                .Where(x => x.score > 0.15)
                .OrderByDescending(x => x.score)
                .Take(5)
                .Select(x => new
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
                    matchPercent = Math.Round(x.score * 100, 2)
                })
                .ToList();

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

            var lostItems = context.Losts.ToList();
            var scoredMatches = new List<(Lost item, double score)>();

            foreach (var lost in lostItems)
            {
                var score = await itemSimilarityService.CalculateLostFoundScoreAsync(lost, found);
                scoredMatches.Add((lost, score));
            }

            var suggestedMatches = scoredMatches
                .Where(x => x.score > 0.15)
                .OrderByDescending(x => x.score)
                .Take(5)
                .Select(x => new
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
                    matchPercent = Math.Round(x.score * 100, 2)
                })
                .ToList();

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
            else
            {
                return BadRequest("Invalid type. Must be 'lost' or 'found'.");
            }

            context.SaveChanges();
            return Ok(new { message = "Image uploaded successfully", imageUrl });
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
            if(type.ToLower() == "found")
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

            var foundItems = context.Founds.ToList();
            var scoredMatches = new List<(Found item, double score)>();

            foreach (var found in foundItems)
            {
                var score = await itemSimilarityService.CalculateLostFoundScoreAsync(lost, found);
                scoredMatches.Add((found, score));
            }

            var suggestedMatches = scoredMatches
                .Where(x => x.score > 0.15)
                .OrderByDescending(x => x.score)
                .Take(5)
                .Select(x => new
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
                    matchPercent = Math.Round(x.score * 100, 2)
                })
                .ToList();

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

            var lostItems = context.Losts.ToList();
            var scoredMatches = new List<(Lost item, double score)>();

            foreach (var lost in lostItems)
            {
                var score = await itemSimilarityService.CalculateLostFoundScoreAsync(lost, found);
                scoredMatches.Add((lost, score));
            }

            var suggestedMatches = scoredMatches
                .Where(x => x.score > 0.15)
                .OrderByDescending(x => x.score)
                .Take(5)
                .Select(x => new
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
                    matchPercent = Math.Round(x.score * 100, 2)
                })
                .ToList();

            return Ok(new
            {
                foundId,
                suggestedMatches
            });
        }
        
    }
}
