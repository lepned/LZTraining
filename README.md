# LZTraining
Download training data from Leela Chess Zero. This is a console-based application for downloading training data from storage.lczero.org. 

Download plan is a model class that is used for defining the various settings needed for downloading the training data. The configuration/download plan is defined in a json-file like this:
```
{
  "StartDate": "2022-11-01",
  "DurationInDays": 5,
  "Url": "https://storage.lczero.org/files/training_data/test80",
  "TargetDir": "E:/LZGames/T80",
  "MaxDownloads": 10,
  "AutomaticRetries": true,
  "AllowToDeleteFailedFiles": false
}
```
StartDate: This represents the start date of the downloads to fetch. It is of type DateTime.
DurationInDays: This represents the duration in days for collecting files to be downloaded.
Url: This represents the URL of the resource to be downloaded. This is fixed and should not be changed.
TargetDir: This represents the target directory where the downloaded files will be saved on your computer.
MaxDownloads: This represents the maximum number of concurrent downloads (max limit is set to 10).
AutomaticRetries: This represents a boolean value indicating whether or not failed downloads should be retried.
AllowToDeleteFailedFiles: This represents a boolean value indicating whether or not failed downloads should be deleted.

The configuration plan is used by the application to define the above settings. Once the configuration plan is set up, it can be used to download the training data. 

After running the program, there are currently three options: downloading missing files by pressing the 'C' key, verifying downloaded files by pressing the 'V' key and stopping the program by pressing the 'Esc' key.
If no arguments are provided upon running the program, then it uses the default configuration plan. If an argument is passed to the program, it is treated as a path to a JSON file containing a custom configuration plan. Upon verifying the downloaded files, there is either a message confirming the successful downloads or a summary of errors that occurred during the download. The program ends after the 'Esc' key is pressed or if there are too many arguments provided.

To run the program, you will need to have .NET 7.0 or later installed. You can download it from the official .NET website. Once you have installed .NET, you can navigate to the directory containing the Program.fs file in your terminal and run the command dotnet run. If you want to use a custom configuration plan, you can you can pass the path to a json-file as an argument like this:

` dotnet run C:\path\to\config.json `



