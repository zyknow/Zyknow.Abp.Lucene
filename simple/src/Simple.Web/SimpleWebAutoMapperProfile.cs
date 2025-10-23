using AutoMapper;
using Simple.Books;

namespace Simple.Web;

public class SimpleWebAutoMapperProfile : Profile
{
    public SimpleWebAutoMapperProfile()
    {
        CreateMap<BookDto, CreateUpdateBookDto>();
        
        //Define your object mappings here, for the Web project
    }
}
