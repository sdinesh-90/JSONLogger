// ------------------------------------------------------------------------- RightAngle --
// Log.cs - Implement the log in JSON format using RightAngle brick interface
// Copyright (c) Metamation India, 2022.
// ---------------------------------------------------------------------------------------
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Petrel;

namespace JSONLogger;

#region class ProductionEvents --------------------------------------------------------------------
[Brick ()]
class ProductionEvents : IPgmState, IInitializable, IWhiteboard {
   #region Implementation -------------------------------------------
   string LogFolder {
      get {
         string? path = mSettings?.Path;
         if (path == null || !Directory.Exists (path)) return Path.Combine (Environment.DataFolder, "Log");
         return path;
      }
   }

   string ProductionPath => Path.Combine (LogFolder, "programtime.json");

   string MachineTimePath => Path.Combine (LogFolder, "machinetime.json");

   string SettingsPath => Path.Combine (Environment.DataFolder, "settings.json");

   void OnTimerElapsed (object? sender, System.Timers.ElapsedEventArgs e) {
      mTimer.Stop ();
      Save ();
      mTimer.Start ();
   }

   void Save () {
      lock (sLock) {
         SaveProductionData ();
         SaveMachineTimeData ();
      }
   }

   void SaveProductionData () {
      var options = new JsonSerializerOptions {
         Encoder = JavaScriptEncoder.Create (UnicodeRanges.All),
      };
      File.WriteAllText (ProductionPath, JsonSerializer.Serialize (mParts.Select (a => new Data (a.Key, a.Value.Time, a.Value.Count)), options));
   }

   void LoadProductionData () {
      string fileName = ProductionPath;
      if (File.Exists (fileName)) {
         var datas = JsonSerializer.Deserialize<List<Data>> (File.ReadAllText (fileName));
         if (datas != null) {
            foreach (var data in datas)
               mParts[data.PartName] = (data.TotalTime, data.PartsDone);
         }
      }
   }

   void SaveMachineTimeData () {
      if (mMacData is null) return;
      string fileName = MachineTimePath;
      if (mMacData.DailyLogs is not DailyMacData[] dailydatas) return;
      IMachineStatus status = MachineStatus;
      DailyMacData? todayTimeData = mMacData.DailyLogs.FirstOrDefault (a => a.Date == DateTime.Today);
      TimeData totalWithoutToday = todayTimeData != null ? mMacData.Total - todayTimeData.Time : mMacData.Total;
      // This is the total machine time data including today
      TimeData total = new (status.ONTime, status.PumpONTime, status.StrokeCount, totalWithoutToday.TotalPartCount + mPartCount);
      // Figure out whether today data is already present in the saved list
      int idx = -1;
      for (int i = 0; i < dailydatas.Length; i++) {
         if (dailydatas[i].Date == DateTime.Today) { idx = i; break; }
      }
      // This is the today machine data
      DailyMacData md = new (DateTime.Today, total - totalWithoutToday);
      // Case where today machine data is already present
      if (idx != -1) dailydatas[idx] = md;
      // Case where we need to add the today's data
      else dailydatas = dailydatas.Concat (new[] { md }).ToArray ();
      File.WriteAllText (fileName, JsonSerializer.Serialize (new MachineData (total, dailydatas)));
   }

   void LoadMachineData () {
      string fileName = MachineTimePath;
      if (File.Exists (fileName))
         mMacData = JsonSerializer.Deserialize<MachineData> (File.ReadAllText (fileName));
      // This corresponds to fresh installation
      mMacData ??= new (new (MachineStatus.ONTime, MachineStatus.PumpONTime, MachineStatus.StrokeCount, 0),
                                  new[] { new DailyMacData (DateTime.Today, TimeData.Empty) });
   }

   void SaveSettingsIfNeeded () {
      if (File.Exists (SettingsPath) || mSettings == null) return; 
      File.WriteAllText (SettingsPath, JsonSerializer.Serialize (mSettings));
   }

   void LoadSettings () {
      string settingsPath = SettingsPath;
      if (File.Exists (settingsPath))
         mSettings = JsonSerializer.Deserialize<Settings> (File.ReadAllText (settingsPath));
      // Load default settings if no file is found
      mSettings ??= new (LogFolder);
      try {
         if (!Directory.Exists (mSettings.Path)) Directory.CreateDirectory (mSettings.Path);
         if (!Directory.Exists (LogFolder)) Directory.CreateDirectory (LogFolder);
      } catch (Exception e) { Console.WriteLine (e.Message); };
   }
   #endregion

   #region Initializable Implementation -----------------------------
   public void Initialize () {
      lock (sLock) {
         LoadSettings ();
         LoadProductionData ();
         LoadMachineData ();
         mTimer.Interval = mSettings!.TimeInterval;
         mTimer.Start ();
         mTimer.Elapsed += OnTimerElapsed;
      }
   }

   public void Uninitialize () {
      lock (sLock) {
         mTimer.Elapsed -= OnTimerElapsed;
         mTimer.Stop ();
         SaveSettingsIfNeeded ();
         Save ();
      }
   }
   #endregion

   #region Whiteboard Implementation --------------------------------
   public IEnvironment Environment { set => sEnvironment = value; get => sEnvironment!; }
   static IEnvironment? sEnvironment;

   public IMachineStatus MachineStatus { set => sMachineStatus = value; get => sMachineStatus!; }
   static IMachineStatus? sMachineStatus;
   #endregion

   #region PgmState Implementation ----------------------------------
   public void BendChanged (string pgmName, int bendNo) { }

   public void ProgramCompleted (string pgmName, int quantity = -1) {
      lock (sLock) {
         if (!mParts.TryGetValue (pgmName, out (TimeSpan Time, int Count) data)) data = (TimeSpan.Zero, 0);
         // Update the time taken
         mTimeTaken += DateTime.Now - mStartTime;
         mParts[pgmName] = (mTimeTaken + data.Time, data.Count + 1);
         mStartTime = DateTime.Now;
         mPartCount++;
      }
   }

   public void ProgramStarted (string pgmName, int bendNo, int quantity = -1) {
      lock (sLock) {
         if (pgmName != mCurrentPart) mTimeTaken = TimeSpan.Zero;
         mStartTime = DateTime.Now;
         mCurrentPart = pgmName;
      }
   }

   public void ProgramStopped (string pgmName, int bendNo, int quantity = -1) {
      lock (sLock) {
         mTimeTaken += DateTime.Now - mStartTime;
      }
   }
   DateTime mStartTime = DateTime.Now;  // Start time of current running program
   TimeSpan mTimeTaken = TimeSpan.Zero; // Total time taken so far running the current program
   #endregion

   #region Record ---------------------------------------------------
   // Used in programtime.json
   record Data ([property: JsonPropertyName ("partName")] string PartName,
                [property: JsonPropertyName ("timeTaken")] TimeSpan TotalTime,
                [property: JsonPropertyName ("partsDone")] int PartsDone);

   // Used in machinetime.json
   record MachineData ([property: JsonPropertyName ("total")] TimeData Total,
                       [property: JsonPropertyName ("dailyLogs")] DailyMacData[] DailyLogs);

   record DailyMacData ([property: JsonPropertyName ("date")] DateTime Date,
                        [property: JsonPropertyName ("data")] TimeData Time);

   record TimeData ([property: JsonPropertyName ("powerOnTime")] TimeSpan PowerOnTime,
                    [property: JsonPropertyName ("pumpOnTime")] TimeSpan PumpOnTime,
                    [property: JsonPropertyName ("stroke")] int Stroke,
                    [property: JsonPropertyName ("partsDone")] int TotalPartCount) {
      public static TimeData Empty => new (TimeSpan.Zero, TimeSpan.Zero, 0, 0);
      public static TimeData operator - (TimeData data1, TimeData data2)
         => new (data1.PowerOnTime - data2.PowerOnTime, data1.PumpOnTime - data2.PumpOnTime,
                 data1.Stroke - data2.Stroke, data1.TotalPartCount - data2.TotalPartCount);
   }

   // Used in Settings.json
   record Settings ([property: JsonPropertyName ("storageLocation")] string Path,
                    [property: JsonPropertyName ("timeInterval")] int TimeInterval = 10000);
   #endregion


   #region Private Data ---------------------------------------------
   static readonly object sLock = new ();

   readonly Dictionary<string, (TimeSpan Time, int Count)> mParts = new (); // Total parts produced in the machine so far
   string mCurrentPart = "";                     // Current part running in the machine
   int mPartCount = 0;                           // Total part count produced so far in the machine
   Settings? mSettings;                          // Settings used to get storage location of log files
   readonly System.Timers.Timer mTimer = new (); // Timer to continuously log data
   MachineData? mMacData;                        // Total machine time data
   #endregion
}
#endregion
