# EmailService

Background service for handling all email related operations in the OpenFarm system.

## Overview

The EmailService handles all email related functionality including: 

1. Sending out automated emails for job notifications.
2. Receiving and processing incoming customer emails via IMAP.
3. Sending out operator reply messages.
4. Sending out auto-reply email responses.
    
It integrates with RabbitMQ for asynchronous message processing, uses SMTP for outbound email delivery, and IMAP for inbound email processing with proper message threading support.

**NOTE**: The service is built for Gmail SMTP/IMAP. Other providers may work if you adjust the server and port environment variables with potential other modifications to the services, these environment variables are exposed for you to use, but any other providers are not officially supported by OpenFarm; requests about non-Gmail providers will be ignored/closed.

## Core Services

### EmailNotificationQueueWorker
**Interface**: None (implements `IHostedService`)

Consumes job notification messages from RabbitMQ queues and sends corresponding templated emails for various job lifecycle events (received, approved, printing, completed, rejected).
Returns NACK on failure to ensure messages are retried.

### IncomingEmailService
**Interface**: None (implements `BackgroundService`)

Polls the configured IMAP server for incoming emails, processes them, and then stores them in the database as message threads. Triggers auto-reply emails when auto-reply rules match.

### EmailDeliveryService
**Interface**: `IEmailDeliveryService`

Sends out templated emails to customers.

### EmailTemplateService
**Interface**: `IEmailTemplateService`

Renders HTML email templates with variable substitution. Preloads and validates all templates at startup for fail-fast behavior. Caches templates in memory for performance.

### EmailAutoReplyService
**Interface**: `IEmailAutoReplyService`

Sends automated replies to incoming customer emails based on time/date rules stored in the database.

## Configuration

### Required Environment Variables

See `.env.template` in the root of this project.

**Email Sending (EmailDeliveryService):**
```
SMTP_SERVER
SMTP_PORT
COMPANY_NAME
GMAIL_EMAIL
GMAIL_APP_PASSWORD
```

**Email Receiving (IncomingEmailService):**
```
IMAP_SERVER
IMAP_PORT
GMAIL_EMAIL
GMAIL_APP_PASSWORD
```

**Template Rendering (EmailTemplateService):**
```
COMPANY_NAME
COMPANY_LOGO_URL
```

**Database:**
```
DATABASE_CONNECTION_STRING
```

### Optional Environment Variables
```
IMAP_POLL_INTERVAL_MINUTES (defaults to 1 minute)
```

## Gmail account and app password usage

In order to use Gmail for email sending and receiving, you will need to create an app password for the account you will be using. You can do this by going to the Google Account Security settings and creating an app password for the account you will be using.

### Example configuration

```bash
SMTP_SERVER=smtp.gmail.com
SMTP_PORT=587
IMAP_SERVER=imap.gmail.com
IMAP_PORT=993
COMPANY_NAME="OpenFarm"
GMAIL_EMAIL="support@your-org.com"
GMAIL_APP_PASSWORD="app-password-for-support-account"
```

## Adding a New Email Template

1. Create the HTML template file in `EmailService/Templates/` directory (e.g., `new_template.html`)
2. Add a constant to `EmailService/Constants/EmailTemplates.cs`:
```csharp
public const string NewTemplate = "new_template.html";
```
3. Use the template in your code:
```csharp
private async Task<bool> OnNewFunctionality(Message message)
{
    return await SendJobEmail(message.JobId, EmailTemplates.NewTemplate, "New Template Subject");
}
```

All the template will be automatically preloaded and validated at startup. If the file is missing, the service will fail to start.

## Available Placeholders

All templates automatically have access to:
- `[COMPANY_NAME]` - Company name from configuration
- `[COMPANY_LOGO_URL]` - Company logo URL from configuration

Additional placeholders are template-specific and passed via the replacements dictionary.
 
 ### Using Custom Placeholders
 
 To add custom placeholders to your template:
 
 1. **In your HTML file**: Use square brackets for your placeholder name, e.g., `[CUSTOM_VALUE]`.
    ```html
    <p>Hello [USER_NAME], your order [ORDER_ID] is ready.</p>
    ```
 
 2. **In your code**: Pass a dictionary of replacements when calling the render method.
    ```csharp
    var replacements = new Dictionary<string, string>
    {
        ["[USER_NAME]"] = "John Doe",
        ["[ORDER_ID]"] = "12345"
    };
    
    // Use the template service to render and delivery service to send:
    var html = _templateService.Render(EmailTemplates.MyTemplate, replacements);
    await _emailDeliveryService.SendAsync(toAddress, "Subject", html);
    ```

## Error Handling

- **Startup**: Missing configuration or templates cause immediate application failure (fail-fast)
- **Runtime**: Template rendering failures and SMTP errors throw exceptions, triggering NACK in the queue worker
- **SMTP**: Automatic retry with exponential backoff (2s, 4s, 8s) before failure, in which after all failures RabbitMQ will then finally reenqueue the message back into the queue.
