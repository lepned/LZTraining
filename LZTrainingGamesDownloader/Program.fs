open System
open System.IO
open System.Threading
open LC0Training.GamesDownloader
open System.Text.Json

//Download training data from LC0
let readDownloadPlan path =
  let json = File.ReadAllText(path)
  JsonSerializer.Deserialize<DownloadPlan>(json)

let defaultPlan = 
  let folder = Directory.GetParent(Environment.CurrentDirectory).Parent.Parent.FullName
  let path = Path.Combine(folder,"Downloadplan.json")
  readDownloadPlan path

//let defaultPlan =  
//  {
//    StartDate = DateTime(2022,12,01)
//    DurationInDays = 6
//    Url = "https://storage.lczero.org/files/training_data/test80"
//    TargetDir= "E:\LZGames\Debug"
//    MaxDownloads = 12 // max number of downloads is limited to 10
//    AutomaticRetries = true
//    AllowToDeleteFailedFiles = false
//  }

let continueDownload (invalidFiles: DownloadedFile array) (plan:DownloadPlan) =
  if invalidFiles.Length = 0 then
    Console.Write Environment.NewLine
    printfn "All files for the given plan successfully downloaded"
    false
  else
    printfn "Total number of oversized files = %d:" invalidFiles.Length
    for file in invalidFiles do
      printfn "%s" file.Desc
    if plan.AllowToDeleteFailedFiles then
      for file in invalidFiles do
        FileInfo(file.TargetFileName).Delete()
      printfn "All oversized files got deleted"
      true
    else
      Console.Write Environment.NewLine
      printfn "Press [R] to remove all oversized files and restart the download. [Any other key] ends the download."                            
      let removeKey = Console.ReadKey()
      if removeKey.Key = ConsoleKey.R then
        Console.Write Environment.NewLine
        printfn "Press [Enter] to confirm operation. [Any other key] ends the download."
        let confirmation = Console.ReadKey()
        if confirmation.Key = ConsoleKey.Enter then
          for file in invalidFiles do
            FileInfo(file.TargetFileName).Delete()
          printfn "All oversized files deleted. Download will restart..."
          true
        else
          false
      else 
        false

let verify plan =
  printfn "Verification started..."
  let passed = createVerificationSummary plan None //(Some "Dec22.txt")
  if passed then 
    printfn "Verification passed" 
  else 
    printfn "Verification failed - look at your bin dir for summary file"
    let list = ResizeArray<DownloadedFile>()
    let failedResumedFiles = collectAllFilesThatNeedToBeResumed plan |> Async.RunSynchronously
    let newFiles = collectAllNewFiles plan |> Async.RunSynchronously
    list.AddRange failedResumedFiles
    list.AddRange newFiles
    printfn "Number of files that need to be downloaded=%d" list.Count
    for file in list do
      let msg = sprintf "File: %s Expected=%d OnDisk=%d" file.Name file.ExpectedSize file.CurrentSize
      printfn "%s" msg


let downloadVerificationLoop plan =    
    let mutable tries = 0
    let rec loop () = async { 
      try
        tries <- tries + 1
        let! resumedFiles = collectAllFilesThatNeedToBeResumed plan
        let (invalidFiles, normalFiles) = resumedFiles |> Array.partition (fun f -> f.CurrentSize > f.ExpectedSize)
        let! newFiles = collectAllNewFiles plan
        let sum = normalFiles.Length + newFiles.Length
        let tmpFiles = ResizeArray<DownloadedFile>()
        //remember to remove this line after debugging
        //tmpFiles.AddRange invalidFiles
        tmpFiles.AddRange normalFiles //resumedFiles
        tmpFiles.AddRange newFiles
        let filesToDownload = tmpFiles |> Seq.toArray |> Array.sortBy(fun e -> e.ExpectedSize)
        let totalSize = filesToDownload |> Array.sumBy(fun e -> e.ExpectedSize)
        Console.Write Environment.NewLine
        printfn "Total number of files to download = %d (size = %s) (number of failed files = %d)" sum (formatFileSize totalSize) invalidFiles.Length
        if sum = 0 then
          if continueDownload invalidFiles plan then
            return! loop ()
          else
            return false
        else if tries > 100 then
          return false
        else 
          do! downloadFilesInChunks filesToDownload plan
          do! Async.Sleep(10000)
          return! loop () 
        
      with
      |_ as e -> 
        failwith $"Program failed to proceed: {e.Message}"
        return false   }
    loop ()

let startProgram plan =
  Console.WriteLine("\nMake sure to review the download plan before proceeding...")
  let planDesc = sprintf "%A" plan
  Console.WriteLine($"\nCurrent download plan in use:\n{planDesc}")
  
  let mutable cont = true  
  while cont do
    Console.WriteLine("\nPress [C] key to download missing files")
    Console.WriteLine("Press [V] key to verify downloads including a summary")
    Console.WriteLine("Press [Esc] key to stop program")
    let keyInfo = Console.ReadKey(true)
    if keyInfo.Key = ConsoleKey.C then
      Console.Write Environment.NewLine
      printfn "Press [Enter] key to confirm downloads. [Any other key] ends the download."
      let keyPressed = Console.ReadKey()
      if keyPressed.Key = ConsoleKey.Enter then
        let continueDownload = downloadVerificationLoop plan |> Async.RunSynchronously
        if continueDownload then
          Console.Write Environment.NewLine
          printfn "You have unresolved invalid files for the download period"
        
      else
        cont <- false
    if keyInfo.Key = ConsoleKey.V then
      verify plan
    if keyInfo.Key = ConsoleKey.Escape then
      cont <- false

  printfn "Application ended"

[<EntryPoint>]
let main args =
  
  //for debugging at the moment
  match args with
  |[||] -> 
    startProgram defaultPlan
  |[|arg1|] -> 
    Console.WriteLine($"Received args={arg1}") 
    let fileInfo = FileInfo(arg1)
    let planFromFile =
      if fileInfo.Exists then
        let jsonPlan = readDownloadPlan arg1
        startProgram jsonPlan
      else        
        startProgram defaultPlan
    printfn "Received args=%s: %A" arg1 planFromFile

  |[|arg1;arg2|] -> printfn "args: %s %s" arg1 arg2
  |_ -> printfn "Too many args provided"
  
    
  // Return 0 to indicate success
  0