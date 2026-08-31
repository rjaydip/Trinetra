using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.OpenApi;

namespace Trinetra.Federation.Api;

internal static class ApiOpenApiExtensions
{
    public static IServiceCollection AddTrinetraOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<SecuritySchemeTransformer>();
            options.AddOperationTransformer<SecurityRequirementTransformer>();
            options.AddDocumentTransformer<TagDescriptionTransformer>();
            options.AddOperationTransformer<PermissionDocumentationTransformer>();
            options.AddOperationTransformer<ProblemDetailsDocumentationTransformer>();
        });

        return services;
    }
}
