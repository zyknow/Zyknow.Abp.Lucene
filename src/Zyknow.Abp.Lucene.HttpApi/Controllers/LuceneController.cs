using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Volo.Abp.AspNetCore.Mvc;
using Zyknow.Abp.Lucene.Dtos;

namespace Zyknow.Abp.Lucene.Controllers;

[Area("lucene")]
[Route("api/lucene")]
public class LuceneController(ILuceneService service, ILogger<LuceneController> logger) : AbpControllerBase
{
    [HttpGet("search/{entity}")]
    public Task<SearchResultDto> SearchAsync([FromRoute] string entity, [FromQuery] SearchQueryInput input)
    {
        logger.LogInformation("HTTP GET search entity={Entity} query={Query} skip={Skip} take={Take} highlight={Highlight}",
            entity, input.Query, input.SkipCount, input.MaxResultCount, input.Highlight);
        return service.SearchAsync(entity, input);
    }

    [HttpGet("search-many")]
    public Task<SearchResultDto> SearchManyAsync([FromQuery] MultiSearchInput input)
    {
        logger.LogInformation("HTTP GET search-many entities=[{Entities}] query={Query} skip={Skip} take={Take} highlight={Highlight}",
            string.Join(',', input.Entities ?? new()), input.Query, input.SkipCount, input.MaxResultCount, input.Highlight);
        return service.SearchManyAsync(input);
    }

    [HttpPost("rebuild/{entity}")]
    public Task<int> RebuildIndexAsync([FromRoute] string entity)
    {
        logger.LogInformation("HTTP POST rebuild entity={Entity}", entity);
        return service.RebuildIndexAsync(entity);
    }

    [HttpPost("rebuild-and-index/{entity}")]
    public Task<int> RebuildAndIndexAllAsync([FromRoute] string entity, [FromQuery] int batchSize = 1000)
    {
        logger.LogInformation("HTTP POST rebuild-and-index entity={Entity} batchSize={Batch}", entity, batchSize);
        return service.RebuildAndIndexAllAsync(entity, batchSize);
    }

    [HttpGet("count/{entity}")]
    public Task<int> GetIndexDocumentCountAsync([FromRoute] string entity)
    {
        logger.LogInformation("HTTP GET count entity={Entity}", entity);
        return service.GetIndexDocumentCountAsync(entity);
    }
}