﻿using KekaBot.kiki.IntentRecognition;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Recognizers.Text.DataTypes.TimexExpression;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Kiki.Dialogs;
using KekaBot.kiki.Bots;
using Kiki;

namespace KekaBot.kiki.Dialogs;

public class DialogFlow : ComponentDialog
{
    private readonly IntentRecognizer _recognizer;
    private readonly ILogger<DialogFlow> _logger;

    public DialogFlow(IntentRecognizer recognizer, ILogger<DialogFlow> logger, LeaveDialog leaveDialog, TicketDialog ticketDialog, TaskDialog taskDialog)
        : base(nameof(DialogFlow))
    {
        this._recognizer = recognizer;
        this._logger = logger; 
        AddDialog(new TextPrompt(nameof(TextPrompt)));
        AddDialog(leaveDialog);
        AddDialog(ticketDialog);
        AddDialog(taskDialog);

        var waterfallSteps = new WaterfallStep[]
        {
                IntroStepAsync,
                ActStepAsync,
                FinalStepAsync,
        };

        AddDialog(new WaterfallDialog(nameof(WaterfallDialog), waterfallSteps));

        // The initial child Dialog to run.
        InitialDialogId = nameof(WaterfallDialog);
    }

    private async Task<DialogTurnResult> IntroStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
    {
        if (!_recognizer.IsConfigured)
        {
            await stepContext.Context.SendActivityAsync(
                MessageFactory.Text("NOTE: CLU is not configured. To enable all capabilities, add 'LuisAppId', 'LuisAPIKey' and 'LuisAPIHostName' to the appsettings.json file.", inputHint: InputHints.IgnoringInput), cancellationToken);

            return await stepContext.NextAsync(null, cancellationToken);
        }

        // Use the text provided in FinalStepAsync or the default if it is the first time.
        var messageText = stepContext.Options?.ToString() ?? "What can I help you with today?\nSay something like \"Take a leave from March 22, 2025 to March 25, 2025.\"";
        var promptMessage = MessageFactory.Text(messageText, messageText, InputHints.ExpectingInput);
        return await stepContext.PromptAsync(nameof(TextPrompt), new PromptOptions { Prompt = promptMessage }, cancellationToken);
    }

    private async Task<DialogTurnResult> ActStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
    {
        if (!_recognizer.IsConfigured)
        {
            // LUIS is not configured, we just run the BookingDialog path with an empty BookingDetailsInstance.
            return await stepContext.BeginDialogAsync(nameof(BookingDialog), new BookingDetails(), cancellationToken);
        }

        // Call LUIS and gather any potential booking details. (Note the TurnContext has the response to the prompt.)
        var cluResponse = await _recognizer.RecognizeAsync(stepContext.Result.ToString(), cancellationToken);
        if (cluResponse == null)
        {
            return await stepContext.NextAsync(null, cancellationToken);
        }

        switch (cluResponse?.Result.Prediction.TopIntent)
        {
            case BotIntents.ApplyLeave:

                // Run the BookingDialog giving it whatever details we have from the LUIS call, it will fill out the remainder.
                var entities = cluResponse.Result.Prediction.Entities;
                var leaveDetails = new LeaveDetails
                {
                    LeaveType = entities.Find(e => string.Equals(e.Category, BotEntities.LeaveType, StringComparison.InvariantCultureIgnoreCase))?.Text,
                    StartDate = entities.Find(e => string.Equals(e.Category, BotEntities.StartDate, StringComparison.InvariantCultureIgnoreCase))?.Text,
                    EndDate = entities.Find(e => string.Equals(e.Category, BotEntities.EndDate, StringComparison.InvariantCultureIgnoreCase))?.Text,
                    Reason = entities.Find(e => string.Equals(e.Category, BotEntities.LeaveReason, StringComparison.InvariantCultureIgnoreCase))?.Text
                };

                return await stepContext.BeginDialogAsync(nameof(LeaveDialog), leaveDetails, cancellationToken);

            case BotIntents.RaiseTicket:
                // We haven't implemented the GetWeatherDialog so we just display a TODO message.
                entities = cluResponse.Result.Prediction.Entities;
                var ticketDetails = new TicketDetails
                {
                    TicketType = entities.Find(e => string.Equals(e.Category, BotEntities.TicketType, StringComparison.InvariantCultureIgnoreCase))?.Text,
                    IssueDescription = entities.Find(e => string.Equals(e.Category, BotEntities.Description, StringComparison.InvariantCultureIgnoreCase))?.Text,
                    TicketTitle = entities.Find(e => string.Equals(e.Category, BotEntities.Title, StringComparison.InvariantCultureIgnoreCase))?.Text
                };
                
                return await stepContext.BeginDialogAsync(nameof(TicketDialog), ticketDetails, cancellationToken);

            case BotIntents.AddTask:
                entities = cluResponse.Result.Prediction.Entities;
                var taskDetails = new TaskDetails
                {
                    TaskName = entities.Find(e => string.Equals(e.Category, BotEntities.TaskName, StringComparison.InvariantCultureIgnoreCase))?.Text
                };

                return await stepContext.BeginDialogAsync(nameof(TaskDialog), taskDetails, cancellationToken);


            default:
                // Catch all for unhandled intents
                var didntUnderstandMessageText = $"Sorry, I didn't get that. Please try asking in a different way (intent was ambiguous or out of context for me.)";
                var didntUnderstandMessage = MessageFactory.Text(didntUnderstandMessageText, didntUnderstandMessageText, InputHints.IgnoringInput);
                await stepContext.Context.SendActivityAsync(didntUnderstandMessage, cancellationToken);
                break;
        }

        return await stepContext.NextAsync(MessageFactory.Text(string.Empty), cancellationToken);
    }

    private async Task<DialogTurnResult> FinalStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
    {
        // If the child dialog ("BookingDialog") was cancelled, the user failed to confirm or if the intent wasn't BookFlight
        // the Result here will be null.
        if (stepContext.Result is BaseDialog result)
        {
            // Now we have all the booking details call the booking service.

            // If the call to the booking service was successful tell the user.

            switch (result.ActionType)
            {
                case BotIntents.ApplyLeave:
                    return await stepContext.ContinueDialogAsync(cancellationToken);
            }
        }

        // Restart the main dialog with a different message the second time around
        var promptMessage = "What else can I do for you?";
        return await stepContext.ReplaceDialogAsync(InitialDialogId, promptMessage, cancellationToken);
    }
}
