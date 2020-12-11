using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Extensions;
using Elsa.Models;
using Elsa.Repositories;
using Elsa.Services;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Elsa.Activities.Timers.Jobs
{
    public class RunWorkflowJob : IJob
    {
        private readonly IWorkflowRunner _workflowRunner;
        private readonly IWorkflowRegistry _workflowRegistry;
        private readonly IWorkflowInstanceRepository _workflowInstanceManager;
        private readonly ILogger _logger;

        public RunWorkflowJob(IWorkflowRunner workflowRunner, IWorkflowRegistry workflowRegistry, IWorkflowInstanceRepository workflowInstanceRepository, ILogger<RunWorkflowJob> logger)
        {
            _workflowRunner = workflowRunner;
            _workflowRegistry = workflowRegistry;
            _workflowInstanceManager = workflowInstanceRepository;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var dataMap = context.MergedJobDataMap;
            var tenantId = dataMap.GetString("TenantId");
            var workflowDefinitionId = dataMap.GetString("WorkflowDefinitionId")!;
            var workflowInstanceId = dataMap.GetString("WorkflowInstanceId");
            var activityId = dataMap.GetString("ActivityId")!;
            var cancellationToken = context.CancellationToken;
            var workflowBlueprint = (await _workflowRegistry.GetWorkflowAsync(workflowDefinitionId, tenantId, VersionOptions.Published, cancellationToken))!;

            if (workflowInstanceId == null)
            {
                if (workflowBlueprint.IsSingleton && await _workflowInstanceManager.IsWorkflowIsAlreadyExecutingAsync(tenantId, workflowDefinitionId, cancellationToken))
                    return;

                await _workflowRunner.RunWorkflowAsync(workflowBlueprint, activityId, cancellationToken: cancellationToken);
            }
            else
            {
                var workflowInstance = await _workflowInstanceManager.GetByIdAsync(workflowInstanceId, cancellationToken);

                if (workflowInstance == null)
                {
                    _logger.LogWarning("Could not run Workflow instance with ID {WorkflowInstanceId} because it appears not yet to be persisted in the database. Rescheduling.", workflowInstanceId);
                    var trigger = context.Trigger;
                    await context.Scheduler.UnscheduleJob(trigger.Key, cancellationToken);
                    var newTrigger = trigger.GetTriggerBuilder().StartAt(trigger.StartTimeUtc.AddSeconds(10)).Build();
                    await context.Scheduler.ScheduleJob(newTrigger, cancellationToken);
                    return;
                }
                
                await _workflowRunner.RunWorkflowAsync(workflowBlueprint, workflowInstance!, activityId, cancellationToken: cancellationToken);
            }
        }
    }
}