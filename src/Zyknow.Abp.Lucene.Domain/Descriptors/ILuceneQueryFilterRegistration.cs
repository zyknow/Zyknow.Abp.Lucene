using Lucene.Net.Search;

namespace Zyknow.Abp.Lucene.Descriptors;

/// <summary>
/// 非泛型的默认查询过滤注册接口：用于把强制过滤规则挂载到 <see cref="EntitySearchDescriptor"/> 上。
/// </summary>
public interface ILuceneQueryFilterRegistration
{
    Task<Query?> BuildAsync(IServiceProvider serviceProvider, string entityName, EntitySearchDescriptor descriptor, object input);
}

internal sealed class DelegateLuceneQueryFilterRegistration<TInput>(
    Func<IServiceProvider, LuceneQueryFilterContext<TInput>, Task<Query?>> buildAsync)
    : ILuceneQueryFilterRegistration
{
    public Task<Query?> BuildAsync(IServiceProvider serviceProvider, string entityName, EntitySearchDescriptor descriptor, object input)
    {
        if (input is not TInput typed)
        {
            // 输入类型不匹配时，不参与过滤（避免多入口调用时抛异常）
            return Task.FromResult<Query?>(null);
        }

        return buildAsync(serviceProvider, new LuceneQueryFilterContext<TInput>(entityName, descriptor, typed));
    }
}



