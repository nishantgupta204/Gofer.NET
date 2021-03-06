﻿using System;
using System.Threading.Tasks;
using Gofer.NET.Utils;
using NCrontab;
using Newtonsoft.Json;

namespace Gofer.NET
{
    public class TaskSchedule
    {
        public string LockKey => $"{nameof(TaskSchedule)}::{TaskKey}::ScheduleLock";

        private string LastRunValueKey => $"{nameof(TaskSchedule)}::{TaskKey}::LastRunValue";
        public bool IsRecurring { get; }
        public string TaskKey { get; }

        private readonly DateTime _startTime;
        private readonly TaskInfo _taskInfo;
        private readonly TimeSpan? _intervalOrOffsetFromNow;
        private readonly TaskQueue _taskQueue;
        private readonly DateTimeOffset? _scheduledTimeAsDateTimeOffset;
        private readonly DateTime? _scheduledTime;
        private readonly string _crontab;

        public TaskSchedule() { }

        public TaskSchedule(
            TaskInfo taskInfo,
            TimeSpan interval,
            TaskQueue taskQueue,
            bool isRecurring, string taskId) : this(taskInfo, taskQueue, isRecurring, taskId)
        {
            _intervalOrOffsetFromNow = interval;
        }

        public TaskSchedule(
            TaskInfo taskInfo,
            DateTimeOffset scheduledTimeAsDateTimeOffset,
            TaskQueue taskQueue,
            bool isRecurring, string taskId) : this(taskInfo, taskQueue, isRecurring, taskId)
        {
            _scheduledTimeAsDateTimeOffset = scheduledTimeAsDateTimeOffset;
        }

        public TaskSchedule(
            TaskInfo taskInfo,
            DateTime scheduledTime,
            TaskQueue taskQueue,
            bool isRecurring, string taskId) : this(taskInfo, taskQueue, isRecurring, taskId)
        {
            _scheduledTime =  scheduledTime;
        }

        public TaskSchedule(
            TaskInfo taskInfo,
            string crontab,
            TaskQueue taskQueue, string taskKey) : this(taskInfo, taskQueue, true, taskKey)
        {
            ValidateCrontab(crontab);
            _crontab = crontab;
        }

        private TaskSchedule(TaskInfo taskInfo, TaskQueue taskQueue, bool isRecurring, string taskKey)
        {
            _taskInfo = taskInfo;
            _startTime = DateTime.UtcNow;
            _taskQueue = taskQueue;

            TaskKey = taskKey;
            IsRecurring = isRecurring;
        }

        /// <summary>
        /// Returns true if the task is run.
        /// </summary>
        public async Task<bool> RunIfScheduleReached()
        {
            var lastRunTime = await GetLastRunTime(LastRunValueKey);

            // If we've already run before, and aren't recurring, dont run again.
            if (lastRunTime.HasValue && !IsRecurring)
            {
                // True is returned so the task removal from schedule is effectively propagated among workers.
                return true;
            }

            if (TaskShouldExecuteBasedOnSchedule(lastRunTime ?? _startTime))
            {
                await SetLastRunTime();
                LogScheduledTaskRun();

                await _taskQueue.Enqueue(_taskInfo);
                return true;
            }

            return false;
        }

        private bool TaskShouldExecuteBasedOnSchedule(DateTime lastRunTime)
        {
            if (_intervalOrOffsetFromNow.HasValue)
            {
                var difference = DateTime.UtcNow - lastRunTime;

                return difference >= _intervalOrOffsetFromNow;
            }

            if (_scheduledTimeAsDateTimeOffset.HasValue)
            {
                var utcScheduledTime = _scheduledTimeAsDateTimeOffset.Value.ToUniversalTime();

                return DateTime.UtcNow >= utcScheduledTime;
            }

            if (_scheduledTime.HasValue)
            {
                var utcScheduledTime = _scheduledTime.Value.ToUniversalTime();

                return DateTime.UtcNow >= utcScheduledTime;
            }

            if (_crontab != null)
            {
                var crontabSchedule = CrontabSchedule.Parse(_crontab, new CrontabSchedule.ParseOptions() { IncludingSeconds = true });

                var nextOccurence = crontabSchedule.GetNextOccurrence(lastRunTime);
                return DateTime.UtcNow >= nextOccurence;
            }

            throw new Exception("Invalid scheduling mechanism used. This is a code bug, should not happen.");

        }

        /// <summary>
        /// Not Thread safe. Use External locking.
        /// </summary>
        private async Task<DateTime?> GetLastRunTime(string lastRunValueKey)
        {
            var jsonString = await _taskQueue.Backend.GetString(lastRunValueKey);

            if (string.IsNullOrEmpty(jsonString))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<DateTime>(jsonString);
        }
            
        /// <summary>
        /// Not thread safe. Use external locking.
        /// </summary>
        private async Task SetLastRunTime()
        {
            await _taskQueue.Backend.SetString(LastRunValueKey, JsonConvert.SerializeObject(DateTime.UtcNow));
        }

        private void LogScheduledTaskRun()
        {
            var intervalString = _intervalOrOffsetFromNow?.ToString() ??
                                 _scheduledTimeAsDateTimeOffset?.ToString() ?? _scheduledTime?.ToString() ?? _crontab;

            Console.WriteLine($"Queueing Scheduled Task for run with interval: {intervalString}");
        }

        private void ValidateCrontab(string crontab)
        {
            try
            {
                var schedule = CrontabSchedule.Parse(crontab, new CrontabSchedule.ParseOptions{IncludingSeconds = true});
                schedule.GetNextOccurrence(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                throw new Exception("Crontab is invalid. See the inner exception for details.", ex);
            }
        }

        /// <summary>
        /// Used to prevent overlap between tasks added at different times but sharing a name.
        /// </summary>
        public async Task ClearLastRunTime()
        {
            await _taskQueue.Backend.DeleteKey(LastRunValueKey);
        }
    }
}