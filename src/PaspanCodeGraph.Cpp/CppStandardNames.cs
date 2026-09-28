namespace PaspanCodeGraph.Cpp;

/// <summary>
/// Names of the C++ standard library (and of the C library) that the parser, which does not read headers, is
/// given as the names of headers: <c>std::string(s)</c> is then a cast and <c>std::get&lt;0&gt;(t)</c> a call.
/// </summary>
internal static class CppStandardNames
{
    public static readonly string[] Types =
    [
        "std::string", "std::wstring", "std::u8string", "std::u16string", "std::u32string", "std::string_view",
        "std::wstring_view", "std::size_t", "std::ptrdiff_t", "std::nullptr_t", "std::byte", "std::max_align_t",
        "std::exception", "std::runtime_error", "std::logic_error", "std::invalid_argument", "std::out_of_range",
        "std::length_error", "std::domain_error", "std::range_error", "std::overflow_error", "std::underflow_error",
        "std::bad_alloc", "std::bad_cast", "std::bad_function_call", "std::bad_optional_access",
        "std::bad_variant_access", "std::system_error", "std::error_code", "std::error_condition",
        "std::error_category", "std::mutex", "std::recursive_mutex", "std::shared_mutex", "std::timed_mutex",
        "std::condition_variable", "std::condition_variable_any", "std::thread", "std::jthread", "std::atomic_flag",
        "std::ostream", "std::istream", "std::iostream", "std::ostringstream", "std::istringstream",
        "std::stringstream", "std::ofstream", "std::ifstream", "std::fstream", "std::streambuf", "std::ios_base",
        "std::ios", "std::wostream", "std::wistream", "std::type_info", "std::type_index", "std::any",
        "std::nullopt_t", "std::monostate", "std::in_place_t", "std::random_device", "std::mt19937", "std::mt19937_64",
        "std::default_random_engine", "std::int8_t", "std::int16_t", "std::int32_t", "std::int64_t", "std::uint8_t",
        "std::uint16_t", "std::uint32_t", "std::uint64_t", "std::intptr_t", "std::uintptr_t", "std::intmax_t",
        "std::uintmax_t", "std::FILE", "std::va_list", "std::time_t", "std::clock_t", "std::tm", "std::errc",
        "std::strong_ordering", "std::weak_ordering", "std::partial_ordering", "std::source_location",
        "std::stop_token", "std::stop_source", "std::latch", "std::locale", "std::regex", "std::smatch", "std::cmatch",
        "std::sregex_iterator", "std::filesystem::path", "std::filesystem::directory_entry",
        "std::filesystem::directory_iterator", "std::filesystem::recursive_directory_iterator",
        "std::filesystem::filesystem_error", "std::chrono::seconds", "std::chrono::milliseconds",
        "std::chrono::microseconds", "std::chrono::nanoseconds", "std::chrono::minutes", "std::chrono::hours",
        "std::chrono::steady_clock", "std::chrono::system_clock", "std::chrono::high_resolution_clock",
        "std::thread::id", "std::streamsize", "std::streampos", "std::fpos_t", "std::wint_t", "std::float_t",
        "std::double_t", "std::nothrow_t", "std::align_val_t", "std::destroying_delete_t", "std::memory_order",
        "size_t", "ptrdiff_t", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t",
        "uint64_t", "intptr_t", "uintptr_t", "intmax_t", "uintmax_t", "FILE", "va_list", "time_t", "clock_t",
        "ssize_t", "off_t", "pid_t", "wchar_t",
    ];

    public static readonly string[] Templates =
    [
        "std::vector", "std::list", "std::deque", "std::forward_list", "std::array", "std::map", "std::multimap",
        "std::set", "std::multiset", "std::unordered_map", "std::unordered_multimap", "std::unordered_set",
        "std::unordered_multiset", "std::stack", "std::queue", "std::priority_queue", "std::pair", "std::tuple",
        "std::optional", "std::variant", "std::expected", "std::unexpected", "std::unique_ptr", "std::shared_ptr",
        "std::weak_ptr", "std::function", "std::move_only_function", "std::basic_string", "std::basic_string_view",
        "std::span", "std::mdspan", "std::initializer_list", "std::allocator", "std::hash", "std::equal_to",
        "std::less", "std::greater", "std::less_equal", "std::greater_equal", "std::plus", "std::minus",
        "std::multiplies", "std::atomic", "std::atomic_ref", "std::future", "std::promise", "std::shared_future",
        "std::packaged_task", "std::lock_guard", "std::unique_lock", "std::scoped_lock", "std::shared_lock",
        "std::chrono::duration", "std::chrono::time_point", "std::reference_wrapper", "std::bitset", "std::complex",
        "std::valarray", "std::basic_ostream", "std::basic_istream", "std::basic_ostringstream",
        "std::basic_istringstream", "std::basic_stringstream", "std::basic_ofstream", "std::basic_ifstream",
        "std::basic_fstream", "std::basic_streambuf", "std::iterator_traits", "std::numeric_limits",
        "std::integral_constant", "std::bool_constant", "std::enable_if", "std::conditional", "std::is_same",
        "std::remove_reference", "std::remove_cv", "std::remove_cvref", "std::decay", "std::underlying_type",
        "std::invoke_result", "std::common_type", "std::char_traits", "std::back_insert_iterator",
        "std::front_insert_iterator", "std::insert_iterator", "std::istream_iterator", "std::ostream_iterator",
        "std::istreambuf_iterator", "std::ostreambuf_iterator", "std::reverse_iterator", "std::move_iterator",
        "std::coroutine_handle", "std::coroutine_traits", "std::generator", "std::flat_map", "std::flat_set",
        "std::formatter", "std::basic_format_string", "std::format_string", "std::default_delete", "std::owner_less",
        "std::enable_shared_from_this", "std::tuple_element", "std::tuple_size", "std::in_place_type_t", "std::ratio",
        "std::basic_regex", "std::conditional_t", "std::enable_if_t", "std::remove_reference_t", "std::decay_t",
        "std::underlying_type_t", "std::invoke_result_t", "std::remove_cv_t", "std::remove_cvref_t",
        "std::remove_pointer_t", "std::common_type_t", "std::add_pointer_t", "std::add_const_t",
        "std::add_lvalue_reference_t", "std::make_unsigned_t", "std::make_signed_t", "std::void_t",
        "std::index_sequence", "std::make_index_sequence", "std::integer_sequence", "std::make_integer_sequence",
        "std::index_sequence_for", "std::tuple_element_t", "std::iter_value_t", "std::iter_reference_t",
        "std::range_value_t", "std::is_integral", "std::is_floating_point", "std::is_arithmetic", "std::is_enum",
        "std::is_class", "std::is_pointer", "std::is_reference", "std::is_const", "std::is_base_of",
        "std::is_convertible", "std::is_constructible", "std::is_trivially_copyable", "std::is_void",
        "std::is_invocable", "std::type_identity", "std::type_identity_t", "std::pointer_traits",
        "std::allocator_traits", "std::array_view", "std::basic_ios", "std::nested_exception", "std::exception_ptr",
    ];

    public static readonly string[] FunctionTemplates =
    [
        "std::make_unique", "std::make_unique_for_overwrite", "std::make_shared", "std::allocate_shared",
        "std::make_pair", "std::make_tuple", "std::get", "std::move", "std::forward", "std::swap", "std::exchange",
        "std::min", "std::max", "std::clamp", "std::declval", "std::static_pointer_cast", "std::dynamic_pointer_cast",
        "std::const_pointer_cast", "std::reinterpret_pointer_cast", "std::any_cast", "std::get_if",
        "std::holds_alternative", "std::bit_cast", "std::duration_cast", "std::time_point_cast", "std::floor",
        "std::ceil", "std::round", "std::make_optional", "std::make_any", "std::construct_at", "std::launder",
        "std::addressof", "std::as_const", "std::to_array", "std::make_obj_using_allocator", "std::is_same_v",
        "std::is_base_of_v", "std::is_convertible_v", "std::is_integral_v", "std::is_floating_point_v",
        "std::is_enum_v", "std::is_pointer_v", "std::is_reference_v", "std::is_const_v",
        "std::is_trivially_copyable_v", "std::is_void_v", "std::is_arithmetic_v", "std::is_class_v",
        "std::is_invocable_v", "std::is_constructible_v", "std::is_default_constructible_v",
        "std::is_nothrow_move_constructible_v", "std::is_nothrow_constructible_v", "std::is_trivially_destructible_v",
        "std::tuple_size_v", "std::is_unsigned_v", "std::is_signed_v", "std::is_lvalue_reference_v",
        "std::is_rvalue_reference_v", "std::is_empty_v", "std::is_final_v", "std::is_abstract_v",
        "std::is_polymorphic_v", "std::is_standard_layout_v", "std::is_aggregate_v", "std::is_array_v",
        "std::is_function_v", "std::is_member_pointer_v", "std::is_scalar_v", "std::is_object_v", "std::is_same_as",
        "std::alignment_of_v", "std::extent_v", "std::rank_v", "std::variant_size_v",
    ];

    public static readonly string[] Concepts =
    [
        "std::integral", "std::floating_point", "std::same_as", "std::derived_from", "std::convertible_to",
        "std::invocable", "std::regular_invocable", "std::predicate", "std::copyable", "std::movable",
        "std::semiregular", "std::regular", "std::totally_ordered", "std::equality_comparable",
        "std::default_initializable", "std::constructible_from", "std::signed_integral", "std::unsigned_integral",
        "std::destructible", "std::input_iterator", "std::output_iterator", "std::forward_iterator",
        "std::bidirectional_iterator", "std::random_access_iterator", "std::contiguous_iterator", "std::sentinel_for",
        "std::three_way_comparable", "std::swappable", "std::assignable_from", "std::ranges::range",
        "std::ranges::input_range", "std::ranges::forward_range", "std::ranges::bidirectional_range",
        "std::ranges::random_access_range", "std::ranges::contiguous_range", "std::ranges::sized_range",
        "std::ranges::view", "std::ranges::viewable_range",
    ];
}
