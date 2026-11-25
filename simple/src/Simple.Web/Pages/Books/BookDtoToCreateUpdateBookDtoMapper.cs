using Riok.Mapperly.Abstractions;
using Simple.Books;
using Volo.Abp.Mapperly;

namespace Simple.Web.Pages.Books;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class BookDtoToCreateUpdateBookDtoMapper : MapperBase<BookDto, CreateUpdateBookDto>
{
    public override partial CreateUpdateBookDto Map(BookDto source);
    public override partial void Map(BookDto source, CreateUpdateBookDto destination);
}
