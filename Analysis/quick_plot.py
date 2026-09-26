import argparse
from pathlib import Path
import matplotlib.pyplot as plt

# Import the existing functions so we don't duplicate code
import plot_quest

def main():
    ap = argparse.ArgumentParser(description="Quickly view QUEST+ plots for a single folder without saving.")
    ap.add_argument("folder", type=Path, nargs='?', help="Path to the folder containing ShotLog_*.csv files")
    args = ap.parse_args()

    folder = args.folder
    if folder is None:
        try:
            import tkinter as tk
            from tkinter import filedialog
            root = tk.Tk()
            root.withdraw() # Hide the main window
            
            print("No folder provided. Please select a folder in the dialog...")
            # Ensure it appears above other windows
            root.attributes('-topmost', True)
            folder_path = filedialog.askdirectory(title="Select Folder with ShotLog CSVs")
            
            if not folder_path:
                print("No folder selected. Exiting.")
                return
            folder = Path(folder_path)
        except ImportError:
            print("Error: No folder provided and tkinter is not available for GUI prompt.")
            return

    if not folder.is_dir():
        print(f"Error: {folder} is not a directory.")
        return

    # Find logs in the specific folder
    datasets = plot_quest.find_shot_logs(folder)
    if not datasets:
        print(f"No ShotLog found in {folder}")
        return

    # Apply nice formatting
    plot_quest.use_publication_style()

    for path, label in datasets:
        print(f"Loading {path.name}...")
        df = plot_quest.load(path)
        if df is None:
            continue

        gamma = plot_quest.guess_rate(df)

        # Generate plots in memory
        plot_quest.fig_curves(df, gamma, label, titles=True)
        plot_quest.fig_convergence(df, label, titles=True)
        plot_quest.fig_sd(df, label, titles=True)
        plot_quest.fig_sd_overlay(df, label, titles=True)
        plot_quest.fig_parameters(df, label, titles=True)
        plot_quest.fig_composite(df, gamma, label, titles=True)

    # Show all generated plots in windows
    print("Opening plots... (Close all plot windows to exit)")
    plt.show()

if __name__ == "__main__":
    main()
