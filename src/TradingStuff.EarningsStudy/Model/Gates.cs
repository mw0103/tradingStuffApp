namespace TradingStuff.EarningsStudy.Model;

/// <summary>
/// The gates of the universe rule, named once so every verb tallies against the same key and the
/// memo's exclusion table is assembled in the order the gates were applied. Name-level gates run
/// first because event discovery needs a CIK; the event-level order follows the pre-registration
/// (event source, timing, security, price, options, quotable, tradable), with "closes present"
/// placed immediately before the price QA that consumes them.
/// </summary>
public static class Gates
{
    // universe verb — name level
    public const string SeedOptionable = "01_seed_current_optionable_list";
    public const string CikMapped = "02_cik_mapping_found";
    public const string PrimaryListing = "03_primary_listing_nyse_nasdaq_nyse_american";

    // events verb — event level
    public const string HasItem202 = "04_8k_item_2_02_filed_in_window";
    public const string NotAmendment = "05_not_8k_a";
    public const string Dedup = "06_one_event_per_cik_quarter_first_wins";

    // timing verb
    public const string TimingClassified = "07_timing_classified_bmo_or_amc";

    // compute verb, in this order
    public const string ClosesPresent = "08_closes_present_pre_entry_entry_exit";
    public const string PriceQa = "09_price_qa_not_quarantined";
    public const string MinimumPrice = "10_entry_close_at_least_10";
    public const string ChainFound = "11_chain_with_front_expiry_spanning_event";
    public const string Quotable = "12_atm_call_and_put_bid_positive";
    public const string Tradable = "13_combined_atm_spread_within_15pct_of_straddle";

    public static readonly IReadOnlyList<string> InOrder =
    [
        SeedOptionable, CikMapped, PrimaryListing,
        HasItem202, NotAmendment, Dedup,
        TimingClassified,
        ClosesPresent, PriceQa, MinimumPrice, ChainFound, Quotable, Tradable
    ];
}
