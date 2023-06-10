open System
open System.IO
open System.Threading
open LC0Training.GamesDownloader


let plan =  
  {
    StartDate = DateTime(2022,12,1)
    Duration = TimeSpan.FromDays 31
    Url = "https://storage.lczero.org/files/training_data/test80"
    TargetDir= "D:/LZGames/T80"
    MaxDownloads = 10 // max number of downloads is limited to 10
    AutomaticRetries = true
    CTS = new CancellationTokenSource()
  }

let verify () =
  printfn "Verification started..."
  let passed = createVerificationSummary plan None //(Some "Dec22.txt")
  if passed then 
    printfn "Verification passed" 
  else 
    printfn "Verification failed - look at your bin dir for summary"

  if not passed then
    let failedResumedFiles = collectAllFilesThatNeedToBeResumed plan |> Async.RunSynchronously
    printfn "Number of files that need to be downloaded=%d" failedResumedFiles.Length
    for file in failedResumedFiles do
      let msg = sprintf "File: %s Expected=%d OnDisk=%d" file.TargetFileName file.ExpectedSize file.CurrentSize
      printfn "%s" msg

let downloadVerificationLoop =  
    let rec loop () = async { 
      let! resumedFiles = collectAllFilesThatNeedToBeResumed plan
      let! newFiles = collectAllNewFiles plan
      let sum = resumedFiles.Length + newFiles.Length
      printfn "Total number of files to download = %d" sum
      if sum = 0 then
        return true
      else
        do! downloadResumedFilesInChunks resumedFiles plan 
        do! downloadNewFilesInChunks newFiles plan
        return! loop () }
    loop ()


let mutable cont = true
while cont do
  Console.WriteLine("\nPress C key to download missing files")
  Console.WriteLine("Press V key to verify downloads including a summary")
  Console.WriteLine("Press Esc key to stop program\n")
  let keyInfo = Console.ReadKey(true)
  if keyInfo.Key = ConsoleKey.C then
    printfn "Press a key to confirm downloads"
    let _ = Console.ReadKey()
    let resultMessage = downloadVerificationLoop |> Async.RunSynchronously
    if resultMessage then
      printfn "All files for the given plan successfully downloaded"
  if keyInfo.Key = ConsoleKey.V then
    verify()
  if keyInfo.Key = ConsoleKey.Escape then
    cont <- false

printfn "Application ended"