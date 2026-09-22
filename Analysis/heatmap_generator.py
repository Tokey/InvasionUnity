import pandas as pd
import matplotlib.pyplot as plt
import numpy as np
import argparse
import sys
import os

def generate_heatmaps(log_path):
    print(f"Loading data from {log_path}...")
    
    if not os.path.exists(log_path):
        print(f"Error: Could not find file {log_path}")
        sys.exit(1)
        
    try:
        cols = ['ufoX', 'towerX', 'shotFired', 'shotHitX', 'spikeFired', 'weapon']
        df = pd.read_csv(log_path, usecols=lambda c: c in cols)
    except Exception as e:
        print(f"Error reading CSV: {e}")
        sys.exit(1)
        
    print(f"Loaded {len(df)} frames.")
    df = df.dropna(subset=['ufoX', 'towerX'])

    # Find the weapon types (e.g. 'shockwave', 'laser')
    weapons = [w for w in df['weapon'].dropna().unique() if str(w).strip() != '']
    if not weapons:
        weapons = ['Unknown']
        df['weapon'] = 'Unknown'

    print(f"Found weapons: {weapons}")

    plt.style.use('ggplot')
    # Create a grid: 4 rows, N columns (one for each weapon type)
    fig, axes = plt.subplots(4, len(weapons), figsize=(6 * len(weapons), 10), sharex=True, squeeze=False)
    fig.suptitle('Experiment Spatial Heatmaps by Weapon Type', fontsize=16)
    bins = 100

    for col_idx, weapon in enumerate(weapons):
        w_df = df[df['weapon'] == weapon]
        
        tower_locs = w_df['towerX']
        player_locs = w_df['ufoX']
        
        spike_mask = w_df['spikeFired'].isin([1, '1', True, 'true', 'True', 1.0])
        shot_mask = w_df['shotFired'].isin([1, '1', True, 'true', 'True', 1.0])
        
        spike_locs = w_df[spike_mask]['ufoX']
        shot_locs = w_df[shot_mask]['shotHitX']

        # Row 0: Tower Locations
        if len(tower_locs) > 1:
            axes[0, col_idx].hist(tower_locs, bins=bins, density=True, color="gray", alpha=0.7)
        axes[0, col_idx].set_title(f"{weapon.capitalize()} - Tower Locations")
        if col_idx == 0: axes[0, col_idx].set_ylabel("Density")

        # Row 1: Player Locations
        if len(player_locs) > 1:
            axes[1, col_idx].hist(player_locs, bins=bins, density=True, color="blue", alpha=0.7)
        axes[1, col_idx].set_title("Player Locations (UFO)")
        if col_idx == 0: axes[1, col_idx].set_ylabel("Density")

        # Row 2: Spike Locations
        if len(spike_locs) > 1:
            axes[2, col_idx].hist(spike_locs, bins=bins, density=True, color="purple", alpha=0.7)
            axes[2, col_idx].plot(spike_locs, np.zeros_like(spike_locs), '|', color="black", ms=15, alpha=0.5)
        else:
            axes[2, col_idx].text(0.5, 0.5, "No spikes", ha='center', va='center', transform=axes[2, col_idx].transAxes)
        axes[2, col_idx].set_title("Stutter (Spike) Locations")
        if col_idx == 0: axes[2, col_idx].set_ylabel("Density")

        # Row 3: Shot Locations
        if len(shot_locs) > 1:
            axes[3, col_idx].hist(shot_locs, bins=bins, density=True, color="red", alpha=0.7)
            axes[3, col_idx].plot(shot_locs, np.zeros_like(shot_locs), '|', color="black", ms=15, alpha=0.5)
        else:
            axes[3, col_idx].text(0.5, 0.5, "No shots", ha='center', va='center', transform=axes[3, col_idx].transAxes)
        axes[3, col_idx].set_title("Shot Locations")
        axes[3, col_idx].set_xlabel("World X Position")
        if col_idx == 0: axes[3, col_idx].set_ylabel("Density")

    plt.tight_layout()
    output_img = log_path.replace(".csv", "_heatmap_by_weapon.png")
    plt.savefig(output_img, dpi=300)
    print(f"\nHeatmap saved to: {output_img}")
    plt.show()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Generate spatial heatmaps from Invasion PlayerLog CSV.")
    default_log = r"H:\Unity Projects\Builds\Invasion\Data\Logs\1_CWsa\PlayerLog_1_CWsa.csv"
    parser.add_argument("log_path", nargs="?", default=default_log, help="Path to the PlayerLog_...csv file")
    args = parser.parse_args()
    
    generate_heatmaps(args.log_path)
