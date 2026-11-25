using Riok.Mapperly.Abstractions;
using Volo.Abp.Mapperly;

namespace Simple.Books;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class BookToBookDtoMapper : MapperBase<Book, BookDto>
{
    public override partial BookDto Map(Book source);
    public override partial void Map(Book source, BookDto destination);
}

// A book input does not own the entity ID, audit fields, or other ABP metadata.
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Source)]
public partial class CreateUpdateBookDtoToBookMapper : MapperBase<CreateUpdateBookDto, Book>
{
    public override partial Book Map(CreateUpdateBookDto source);
    public override partial void Map(CreateUpdateBookDto source, Book destination);
}
