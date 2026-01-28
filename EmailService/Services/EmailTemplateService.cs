using System.Collections.Concurrent;
using System.Reflection;

using EmailService.Constants;
using EmailService.Interfaces;

namespace EmailService.Services;

/// <summary>
/// Renders email templates with placeholder substitution and caching.
/// </summary>
/// <remarks>
/// <para>
/// This service loads HTML email templates from disk and performs placeholder replacement.
/// Templates are cached in memory after first load for performance.
/// </para>
/// <para>
/// Standard placeholders automatically added to all templates:
/// </para>
/// <list type="bullet">
/// <item><term>[COMPANY_NAME]</term><description>The configured company name</description></item>
/// <item><term>[COMPANY_LOGO_URL]</term><description>URL to the company logo image</description></item>
/// </list>
/// </remarks>
public sealed class EmailTemplateService : IEmailTemplateService
{
    /// <summary>Thread-safe cache of loaded template content keyed by filename.</summary>
    private readonly ConcurrentDictionary<string, string> _cache = new();

    private readonly ILogger<EmailTemplateService> _logger;
    private readonly string _companyName;
    private readonly string _companyLogoUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailTemplateService"/> class.
    /// </summary>
    /// <param name="configuration">Application configuration containing company settings.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when COMPANY_NAME or COMPANY_LOGO_URL configuration is missing,
    /// or when a template file defined in <see cref="EmailTemplates"/> cannot be loaded.
    /// </exception>
    public EmailTemplateService(IConfiguration configuration, ILogger<EmailTemplateService> logger)
    {
        _logger = logger;
        _companyName = configuration["COMPANY_NAME"]
            ?? throw new InvalidOperationException("COMPANY_NAME environment variable is not set");
        _companyLogoUrl = configuration["COMPANY_LOGO_URL"]
            ?? throw new InvalidOperationException("COMPANY_LOGO_URL environment variable is not set");

        PreloadTemplates();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The template is loaded from cache (or disk on first access) and all placeholder
    /// tokens in the replacements dictionary are substituted with their values.
    /// </para>
    /// <para>
    /// Company name and logo URL placeholders are automatically added if not already
    /// present in the replacements dictionary.
    /// </para>
    /// </remarks>
    public string Render(string templateFile, IDictionary<string, string> replacements)
    {
        // Get template from memory cache (or load from disk on first access)
        var html = _cache.GetOrAdd(templateFile, LoadTemplate);

        // Add standard company branding placeholders (if not overridden)
        replacements.TryAdd("[COMPANY_NAME]", _companyName);
        replacements.TryAdd("[COMPANY_LOGO_URL]", _companyLogoUrl);

        // Perform all placeholder substitutions in a single pass
        return replacements.Aggregate(html, (current, kv) => current.Replace(kv.Key, kv.Value));
    }

    /// <summary>
    /// Preloads all templates defined in <see cref="EmailTemplates"/> into the cache.
    /// </summary>
    /// <remarks>
    /// This method uses reflection to discover all public static string constants in the
    /// <see cref="EmailTemplates"/> class and loads each corresponding template file.
    /// This ensures all templates are valid and accessible at service startup rather than
    /// failing at runtime when an email needs to be sent.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if any template file cannot be loaded, preventing the service from starting
    /// with missing or inaccessible templates.
    /// </exception>
    private void PreloadTemplates()
    {
        // Use reflection to discover all template file constants in EmailTemplates class
        // This validates templates exist at startup rather than failing at runtime
        var templateFields = typeof(EmailTemplates)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(fi => fi.IsLiteral && !fi.IsInitOnly && fi.FieldType == typeof(string));

        foreach (var field in templateFields)
        {
            if (field.GetValue(null) is not string templateFile) 
                continue;

            try
            {
                // Load template from disk and cache in memory
                var content = LoadTemplate(templateFile);
                _cache.TryAdd(templateFile, content);
            }
            catch (Exception ex)
            {
                // Fail fast: missing templates should prevent service startup
                _logger.LogError(ex, "Failed to preload template {TemplateFile} defined in {FieldName}",
                    templateFile, $"Constants.EmailTemplates.{field.Name}");
                throw new InvalidOperationException(
                    $"Failed to preload template '{templateFile}' defined in Constants.EmailTemplates.{field.Name}", ex);
            }
        }

        _logger.LogDebug("Preloaded {Count} email templates", _cache.Count);
    }

    /// <summary>
    /// Loads a template file from the Templates directory.
    /// </summary>
    /// <param name="templateFile">The filename of the template to load (e.g., "job_received.html").</param>
    /// <returns>The raw HTML content of the template.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the template file does not exist.</exception>
    /// <exception cref="IOException">Thrown if the file cannot be read.</exception>
    private string LoadTemplate(string templateFile)
    {
        // Templates are stored in the Templates directory alongside the application
        var path = Path.Combine(AppContext.BaseDirectory, "Templates", templateFile);

        if (!File.Exists(path))
        {
            _logger.LogError("Email template not found at {Path}", path);
            throw new FileNotFoundException($"Email template not found at: {path}", path);
        }

        try
        {
            // Read entire template file as raw HTML string
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading template file at {Path}", path);
            throw new IOException($"Error reading template file at: {path}", ex);
        }
    }
}
