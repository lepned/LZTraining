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

let passed = createVerificationSummary plan
if passed then 
  printfn "Verification passed" 
else 
  printfn "Verification failed - look at your bin dir for summary"

if not passed then
  let failedFiles = collectAllFilesThatFailedSizeVerification plan |> Async.RunSynchronously
  printfn "Number of files that need to be downloaded=%d" failedFiles.Length
  for file in failedFiles do
    let msg = sprintf "File: %s Expected=%d OnDisk=%d" file.TargetFileName file.ExpectedSize file.CurrentSize
    printfn "%s" msg

let filesChecked = async {  
    do! downloadAllFilesInChunks plan
    do! downloadAllVerifiedFailedFilesInChunks plan 
    return "Done with download verification loop"
    }

//let resultMessage = filesChecked |> Async.RunSynchronously

printfn "Press a key to continue"
let n = Console.ReadLine()