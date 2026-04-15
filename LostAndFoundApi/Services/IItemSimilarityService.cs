using LostAndFoundApi.Models;

namespace LostAndFoundApi.Services
{
    public interface IItemSimilarityService
    {
        Task<double> CalculateLostFoundScoreAsync(Lost lost, Found found);
    }
}
