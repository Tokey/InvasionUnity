import argparse
import math
from pathlib import Path
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_quest

def analyze_folder(folder_path):
    datasets = plot_quest.find_shot_logs(folder_path)
    if not datasets:
        print(f"No ShotLog found in {folder_path}")
        return

    targets = list(range(5, 16))
    
    # Store records: {"wep": weapon_label, "target": SD, "rounds": int}
    records = []
    
    # Store raw SD data for individual plotting: label -> list of (wep, sd_series)
    raw_sd_data = {}

    for path, label in datasets:
        df = plot_quest.load(path)
        if df is None:
            continue

        for i, (block_index, block) in enumerate(plot_quest.main_rows(df).groupby("blockIndex", sort=True)):
            wep = plot_quest.block_label(block, block_index)
            
            if label not in raw_sd_data:
                raw_sd_data[label] = []
            raw_sd_data[label].append((wep, block["sd"].reset_index(drop=True), block["isHit"].reset_index(drop=True)))
            
            for t in targets:
                converged = block[block["sd"] <= t]
                if not converged.empty:
                    first = converged.iloc[0]
                    idx = np.where(block.index == first.name)[0][0] + 1
                    is_converged = 1
                else:
                    idx = len(block)
                    is_converged = 0
                    
                records.append({
                    "weapon": wep,
                    "target_sd": t,
                    "rounds": idx,
                    "converged": is_converged,
                    "participant": label
                })

    if not records:
        print("No convergence data found.")
        return

    df_records = pd.DataFrame(records)
    
    # Calculate global average
    global_avg = df_records.groupby("target_sd").agg(
        mean_rounds=("rounds", "mean"),
        min_rounds=("rounds", "min"),
        max_rounds=("rounds", "max"),
        total_samples=("rounds", "count"),
        converged_count=("converged", "sum")
    ).reset_index()
    
    global_avg["success_rate"] = (global_avg["converged_count"] / global_avg["total_samples"]) * 100
    
    # Calculate per-weapon average
    wep_avg = df_records.groupby(["weapon", "target_sd"])["rounds"].mean().unstack(level="weapon")
    
    # Print Table
    print("\n" + "="*80)
    print("AVERAGE ROUNDS REQUIRED TO HIT TARGET SD (Global)")
    print("Note: Blocks that fail to reach the target are capped at their max rounds.")
    print("="*80)
    print(f"{'Target SD':<12} | {'Avg Rounds':<12} | {'Success %':<10} | {'Min':<6} | {'Max':<6} | {'Samples'}")
    print("-" * 80)
    for _, row in global_avg.iterrows():
        print(f"{int(row['target_sd']):<12} | {row['mean_rounds']:<12.1f} | {row['success_rate']:<9.1f}% | {int(row['min_rounds']):<6} | {int(row['max_rounds']):<6} | {int(row['total_samples'])}")
    print("="*80 + "\n")

    # Plot
    plot_quest.use_publication_style()
    fig, ax = plt.subplots(figsize=(plot_quest.DOUBLE_COL, 3.5))
    
    # Plot individual condition lines
    for i, wep in enumerate(wep_avg.columns):
        color = plot_quest.BLOCK_COLORS[i % len(plot_quest.BLOCK_COLORS)]
        # We need to drop NaNs in case some condition didn't hit a target
        wep_data = wep_avg[wep].dropna()
        ax.plot(wep_data.index, wep_data, marker="o", markersize=4, 
                color=color, linewidth=1.5, alpha=0.8, label=wep)
                
    # Plot global average thick line
    ax.plot(global_avg["target_sd"], global_avg["mean_rounds"], marker="D", markersize=6,
            color="black", linewidth=3.0, zorder=10, label="Global Average")
            
    ax.set_title("Average trials required to reach Target SD", fontweight="bold")
    ax.set_xlabel("Target Posterior SD (ms)")
    ax.set_ylabel("Number of Trials (per block)")
    ax.set_xticks(targets)
    ax.grid(True, alpha=0.3)
    ax.legend(frameon=False, loc="upper right")
    
    plt.tight_layout()
    # Save the plot
    plt.savefig("sd_target_analysis.png")
    print("Saved sd_target_analysis.png")
    
    # ---------------------------------------------------------
    # Plot 2: Individual SD Convergence Grid
    # ---------------------------------------------------------
    participants = list(raw_sd_data.keys())
    num_p = len(participants)
    
    if num_p > 0:
        cols = min(4, num_p)
        rows = math.ceil(num_p / cols)
        
        fig2, axes = plt.subplots(rows, cols, figsize=(plot_quest.SINGLE_COL * cols, 2.5 * rows), sharey=True, squeeze=False)
        axes = axes.flatten()
        
        # Build a consistent color mapping for weapons
        unique_weps = []
        for p in participants:
            for wep, _, _ in raw_sd_data[p]:
                if wep not in unique_weps:
                    unique_weps.append(wep)
        
        wep_colors = {w: plot_quest.BLOCK_COLORS[i % len(plot_quest.BLOCK_COLORS)] for i, w in enumerate(unique_weps)}
        
        for idx, label in enumerate(participants):
            ax_sub = axes[idx]
            for wep, sd_series, is_hit_series in raw_sd_data[label]:
                color = wep_colors[wep]
                ax_sub.plot(sd_series.index, sd_series.values, color=color, 
                            linewidth=1.5, alpha=0.8, label=wep)
                
                # Plot hits and misses
                is_hit_bool = is_hit_series.astype(bool)
                hits = sd_series[is_hit_bool]
                misses = sd_series[~is_hit_bool]
                
                # We do not add labels for the scatter points to avoid cluttering the legend
                ax_sub.scatter(hits.index, hits.values, color='green', marker='o', s=12, zorder=3, alpha=0.7)
                ax_sub.scatter(misses.index, misses.values, color='red', marker='x', s=12, zorder=3, alpha=0.7)
            
            ax_sub.axhline(y=6, color=plot_quest.RULE, linestyle="--", linewidth=1.0, zorder=0)
            ax_sub.set_title(label, fontsize=9, fontweight="bold")
            
            if idx % cols == 0:
                ax_sub.set_ylabel("Posterior SD")
            if idx >= num_p - cols:
                ax_sub.set_xlabel("Trial Number")
                
            # Avoid duplicate labels in legend per subplot
            handles, labels = ax_sub.get_legend_handles_labels()
            by_label = dict(zip(labels, handles))
            ax_sub.legend(by_label.values(), by_label.keys(), frameon=False, fontsize=8)
            
        # Hide any unused subplots
        for idx in range(num_p, len(axes)):
            axes[idx].set_visible(False)
            
        plt.tight_layout()
        plt.savefig("sd_all_runs.png")
        print("Saved sd_all_runs.png")

    print("Opening plots...")
    plt.show()

def main():
    ap = argparse.ArgumentParser(description="Analyze average rounds to reach SD targets.")
    ap.add_argument("folder", type=Path, nargs='?', help="Path to folder with ShotLog_*.csv files")
    args = ap.parse_args()

    folder = args.folder
    if folder is None:
        try:
            import tkinter as tk
            from tkinter import filedialog
            root = tk.Tk()
            root.withdraw()
            root.attributes('-topmost', True)
            folder_path = filedialog.askdirectory(title="Select Folder with ShotLog CSVs")
            if not folder_path:
                print("No folder selected.")
                return
            folder = Path(folder_path)
        except ImportError:
            print("No folder provided.")
            return

    if not folder.is_dir():
        print(f"Error: {folder} is not a directory.")
        return

    analyze_folder(folder)

if __name__ == "__main__":
    main()
