using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Zyknow.Abp.Lucene.Interceptors;

namespace Zyknow.Abp.Lucene;

public abstract class LuceneAbpDbContext<TDbContext>(DbContextOptions<TDbContext> options)
    : AbpDbContext<TDbContext>(options)
    where TDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var luceneEfChangeInterceptor = LazyServiceProvider.LazyGetRequiredService<LuceneEfChangeInterceptor>();
        optionsBuilder.AddInterceptors([luceneEfChangeInterceptor]);
        base.OnConfiguring(optionsBuilder);
    }
}