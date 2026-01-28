namespace EmailService.Interfaces;

/// <summary>
/// Interface for email template service.
/// </summary>
public interface IEmailTemplateService
{
    /// <summary>
    /// Renders an email template with the given replacements.
    /// </summary>
    /// <param name="templateFile">The template file to render.</param>
    /// <param name="replacements">The replacements to use in the template.</param>
    /// <returns>The rendered email template.</returns>
    string Render(string templateFile, IDictionary<string, string> replacements) =>
        throw new NotImplementedException();
}